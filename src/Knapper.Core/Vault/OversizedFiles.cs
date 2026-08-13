using System.Diagnostics;

namespace Knapper.Core.Vault;

/// <summary>
/// Vault files Obsidian Sync will not carry.
///
/// The one implementation, deliberately: <c>/health</c> and `knapper doctor`
/// both report this, and two walks with two dot-skip rules would drift into
/// disagreeing about whether the vault is clean — the same failure shape the
/// lister/ripgrep differential test exists to prevent.
///
/// This is the BACKSTOP, not the guard. <c>VaultMutationService</c> refuses
/// Knapper's own oversized writes; nothing stops one arriving from Dan's Macs
/// or the Obsidian app, and Sync says nothing useful when it happens — it logs
/// the rejection and prints "Fully synced" in the same millisecond (measured
/// CT 106, 2026-08-13).
/// </summary>
public static class OversizedFiles
{
    /// <summary>
    /// Wall-clock bound on a single scan, matching the sibling ripgrep probe's
    /// 5s: this walk runs on the <c>/health</c> and <c>/up</c> request path,
    /// which the host monitor polls every 5 minutes, so a walk that will not
    /// finish must degrade health rather than hang it. It bounds a walk that
    /// keeps RETURNING entries (a pathological tree, an unforeseen cycle); a
    /// single syscall blocked in the kernel is not interruptible from here.
    /// </summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Vault-relative paths over <paramref name="limitBytes"/>, ordinal-sorted.
    /// Throws <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>
    /// on a failed walk, and <see cref="TimeoutException"/> when
    /// <paramref name="budget"/> expires, rather than returning a short list —
    /// a partial scan reported as "none found" is the same lie as an empty
    /// search claiming exhaustive coverage. All three mean one thing to a
    /// caller: "could not tell". Callers decide what to do with it, and both
    /// of them must distinguish it from "scanned, none found".
    ///
    /// Dot-entries are skipped at every depth, matching what queries can see:
    /// .git packfiles and .obsidian plugin bundles routinely exceed the
    /// ceiling, none of them sync, and listing them would be permanent noise
    /// that trains the reader to ignore the warning. That is also why
    /// <c>.trash/</c> is absent: <c>vault_delete</c> is soft, so a file that
    /// arrived over-ceiling and was then deleted still sits there — reporting
    /// it would be an alert about a file the human has already dealt with and
    /// cannot clear through any tool.
    ///
    /// Symlinks are skipped, never followed, like every other vault walk
    /// (<c>VaultFileLister</c>, <c>ConflictDetector</c>; the resolver and lock
    /// manager reject them outright). This walk was the exception once: a
    /// directory-symlink CYCLE inside the vault made it non-terminating, on
    /// the request path of the endpoint whose whole job is to notice that
    /// something is wrong.
    /// </summary>
    public static IReadOnlyList<string> Scan(string root, long limitBytes, TimeSpan? budget = null)
    {
        var allowed = budget ?? DefaultBudget;
        var started = Stopwatch.GetTimestamp();
        var found = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            RequireBudget();
            foreach (var entry in new DirectoryInfo(stack.Pop()).EnumerateFileSystemInfos())
            {
                RequireBudget();
                if (entry.Name.StartsWith('.') || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                if (entry is DirectoryInfo dir)
                    stack.Push(dir.FullName);
                else if (entry is FileInfo file && file.Length > limitBytes)
                    found.Add(Path.GetRelativePath(root, file.FullName));
            }
        }
        found.Sort(StringComparer.Ordinal);
        return found;

        void RequireBudget()
        {
            if (Stopwatch.GetElapsedTime(started) >= allowed)
            {
                throw new TimeoutException(
                    $"oversized-file scan exceeded its {allowed.TotalSeconds:0.###}s budget — " +
                    "reporting 'could not tell' rather than a partial walk");
            }
        }
    }
}
