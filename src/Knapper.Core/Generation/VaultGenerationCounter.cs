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
    // Per-PROCESS, and deliberately so: it exists to answer "did the vault
    // move DURING this query", which never spans a restart. It starts at zero
    // on every start, so a value from before a restart compares as though the
    // vault went backwards — the `initialize` instructions state the lifetime
    // because they advertise the span to agents.
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
            // ANY dot-segment, matching the visibility contract: queries
            // cannot see hidden entries at any depth, so their churn
            // (.DS_Store on macOS, .obsidian workspace saves, git objects)
            // must not flip changed_during_query — constant over-reporting
            // erodes the signal agents are told to re-run on. Knapper temps
            // are dot-prefixed too, so this covers them.
            if (segment.StartsWith('.'))
                return true;
        }
        return false;
    }

    public void Dispose() => _watcher?.Dispose();
}
