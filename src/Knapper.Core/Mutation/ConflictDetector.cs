using System.Diagnostics;
using Knapper.Core.Vault;

namespace Knapper.Core.Mutation;

/// <summary>
/// The Obsidian Sync conflict gate (brief §5/§8): when Sync materializes a
/// <c>Name (Conflicted copy ...).md</c> file, mutations to the original AND
/// the conflict sibling are blocked until a human reconciles. Agents never
/// silently pick a canonical branch — that rule is the reason the vault's
/// sync strategy is "conflict files, never automatic merge".
/// </summary>
public sealed class ConflictDetector(VaultPathResolver resolver)
{
    private const string Marker = " (Conflicted copy";

    /// <summary>
    /// The second conflict family, made by Knapper itself: when a raced
    /// replace displaces an external version that cannot be restored to the
    /// canonical pathname, `AtomicFile` republishes it VISIBLY under this
    /// marker (a hidden-only surviving version would be invisible to queries,
    /// Sync, and git — data loss wearing a failure receipt). Deliberately NOT
    /// Sync's own marker: forging "(Conflicted copy" would misattribute the
    /// event. Both families mean the same thing operationally — two versions
    /// exist, a human picks, mutations to the note block until then — so the
    /// gate, the sibling check, and the health walk treat them identically.
    /// </summary>
    internal const string DisplacedMarker = " (Knapper displaced";

    private static bool IsConflictName(string name) =>
        name.Contains(Marker, StringComparison.Ordinal)
        || name.Contains(DisplacedMarker, StringComparison.Ordinal);

    /// <summary>Blocks mutations to a conflict file, or to a file that has a conflict sibling.</summary>
    public void AssertNotConflicted(VaultPath path)
    {
        var name = System.IO.Path.GetFileName(path.Relative);
        if (IsConflictName(name))
        {
            throw new KnapperException(VaultErrorCode.MutationBlocked,
                $"'{path.Relative}' is a conflict file (Sync conflict copy or Knapper-displaced version) — " +
                "a human must reconcile it; agents never resolve conflict files");
        }

        var directory = System.IO.Path.GetDirectoryName(path.Absolute)!;
        if (!Directory.Exists(directory))
            return;
        var stem = System.IO.Path.GetFileNameWithoutExtension(name);
        var sibling = Directory.EnumerateFiles(directory)
            .Select(System.IO.Path.GetFileName)
            .FirstOrDefault(n => n!.StartsWith(stem + Marker, StringComparison.Ordinal)
                || n.StartsWith(stem + DisplacedMarker, StringComparison.Ordinal));
        if (sibling is not null)
        {
            throw new KnapperException(VaultErrorCode.MutationBlocked,
                $"'{path.Relative}' has an unresolved conflict sibling ('{sibling}') — " +
                "mutations to both are blocked until a human reconciles");
        }
    }

    /// <summary>
    /// Wall-clock bound on a single scan, matching <c>OversizedFiles</c>'s and
    /// the ripgrep probe's 5s — this walk runs on the <c>/health</c> and
    /// <c>/up</c> request path, uncached, on every monitor poll. Same contract
    /// as its sibling: a walk that will not finish must degrade health, never
    /// hang the endpoint whose whole job is to notice that something is wrong.
    /// </summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// All conflict files currently in the vault (for health reporting/alerting).
    ///
    /// <para>Throws <see cref="IOException"/> /
    /// <see cref="UnauthorizedAccessException"/> on a failed walk and
    /// <see cref="TimeoutException"/> when <paramref name="budget"/> expires,
    /// rather than returning the short list — exactly like
    /// <c>OversizedFiles.Scan</c>, and for the same reason: "could not tell"
    /// must never arrive at a caller looking like "scanned, none found". Here
    /// that lie is the more expensive of the two, because a missed conflict
    /// file is the difference between a 503 and a green board.</para>
    ///
    /// <para>Symlinks are skipped (never followed) and dot-entries skipped at
    /// every depth, matching every other vault walk. The reparse-point filter
    /// already rules out the cycle that made the oversized walk
    /// non-terminating; the budget covers what it cannot — a tree that has
    /// outgrown the design assumption, or a filesystem that has gone slow.</para>
    /// </summary>
    public IReadOnlyList<string> ScanAll(TimeSpan? budget = null)
    {
        var allowed = budget ?? DefaultBudget;
        var started = Stopwatch.GetTimestamp();
        var results = new List<string>();
        Walk(new DirectoryInfo(resolver.Root), "");
        results.Sort(StringComparer.Ordinal);
        return results;

        void Walk(DirectoryInfo dir, string relBase)
        {
            RequireBudget();
            foreach (var entry in dir.EnumerateFileSystemInfos())
            {
                RequireBudget();
                if (entry.Name.StartsWith('.'))
                    continue;
                var rel = relBase.Length == 0 ? entry.Name : relBase + '/' + entry.Name;
                // The conflict judgement is by NAME, before the reparse-point
                // skip: a displaced recovery object can itself be a symlink
                // (AtomicFile publishes the survivor no-follow), and skipping
                // it here would report a green board over a note the gate is
                // blocking. Recognizing the entry never follows it; recursion
                // still refuses every reparse point, so a directory-symlink
                // cycle stays impossible.
                var reparse = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                if (entry is DirectoryInfo sub && !reparse)
                    Walk(sub, rel);
                else if (IsConflictName(entry.Name))
                    results.Add(rel); // files, file-symlinks, AND dir-symlinks — every non-recursed shape
            }
        }

        void RequireBudget()
        {
            if (Stopwatch.GetElapsedTime(started) >= allowed)
            {
                throw new TimeoutException(
                    $"conflict-file scan exceeded its {allowed.TotalSeconds:0.###}s budget — " +
                    "reporting 'could not tell' rather than a partial walk");
            }
        }
    }
}
