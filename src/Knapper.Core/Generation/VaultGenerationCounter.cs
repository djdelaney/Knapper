using Knapper.Core.Vault;

namespace Knapper.Core.Generation;

/// <summary>
/// Monotonic vault generation (brief §6): incremented on MCP mutations
/// (explicitly, by the transaction layer) and on filesystem-watcher events
/// (Obsidian Sync deliveries, external writers). Every query response
/// carries <c>generation_start</c>/<c>generation_end</c> so an agent can see
/// that the vault moved under its feet; the per-file SHA-256 remains the
/// actual mutation precondition — this counter is a freshness signal, not a
/// lock.
///
/// <para>Events under control dirs (.git/.obsidian/.trash) and Knapper temp
/// files don't count: queries can't see those paths, so counting them would
/// make <c>changed_during_query</c> fire on every git commit and every
/// workspace.json save Sync delivers. A watcher buffer overflow DOES count —
/// when events were lost, "nothing changed" is unknowable, and the counter
/// must move (unknown is never reported as unchanged).</para>
/// </summary>
public sealed class VaultGenerationCounter : IDisposable
{
    private long _generation;
    private FileSystemWatcher? _watcher;

    public long Current => Interlocked.Read(ref _generation);

    /// <summary>Called by the transaction layer on every successful mutation.</summary>
    public long Increment() => Interlocked.Increment(ref _generation);

    /// <summary>
    /// Start a counter fed by a recursive filesystem watcher on the vault
    /// root (inotify on the production LXC, FSEvents on dev macOS).
    /// </summary>
    public static VaultGenerationCounter StartWatching(string vaultRoot)
    {
        var counter = new VaultGenerationCounter();
        var watcher = new FileSystemWatcher(vaultRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        var root = vaultRoot.TrimEnd('/');
        void OnEvent(string fullPath)
        {
            if (!IsControlPath(ToRelative(root, fullPath)))
                counter.Increment();
        }
        watcher.Created += (_, e) => OnEvent(e.FullPath);
        watcher.Changed += (_, e) => OnEvent(e.FullPath);
        watcher.Deleted += (_, e) => OnEvent(e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            OnEvent(e.OldFullPath);
            OnEvent(e.FullPath);
        };
        // Buffer overflow = events lost = we cannot know nothing changed.
        watcher.Error += (_, _) => counter.Increment();
        watcher.EnableRaisingEvents = true;
        counter._watcher = watcher;
        return counter;
    }

    private static string ToRelative(string root, string fullPath) =>
        fullPath.StartsWith(root + '/', StringComparison.Ordinal)
            ? fullPath[(root.Length + 1)..]
            : fullPath;

    internal static bool IsControlPath(string relativePath)
    {
        foreach (var segment in relativePath.Split('/'))
        {
            if (segment is ".git" or ".obsidian" or ".trash")
                return true;
            if (segment.StartsWith(AtomicFile.TempPrefix, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public void Dispose() => _watcher?.Dispose();
}
