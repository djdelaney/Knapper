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

    /// <summary>Blocks mutations to a conflict file, or to a file that has a conflict sibling.</summary>
    public void AssertNotConflicted(VaultPath path)
    {
        var name = System.IO.Path.GetFileName(path.Relative);
        if (name.Contains(Marker, StringComparison.Ordinal))
        {
            throw new KnapperException(VaultErrorCode.MutationBlocked,
                $"'{path.Relative}' is a Sync conflict file — a human must reconcile it; " +
                "agents never resolve conflict files");
        }

        var directory = System.IO.Path.GetDirectoryName(path.Absolute)!;
        if (!Directory.Exists(directory))
            return;
        var stem = System.IO.Path.GetFileNameWithoutExtension(name);
        var sibling = Directory.EnumerateFiles(directory)
            .Select(System.IO.Path.GetFileName)
            .FirstOrDefault(n => n!.StartsWith(stem + Marker, StringComparison.Ordinal));
        if (sibling is not null)
        {
            throw new KnapperException(VaultErrorCode.MutationBlocked,
                $"'{path.Relative}' has an unresolved Sync conflict sibling ('{sibling}') — " +
                "mutations to both are blocked until a human reconciles");
        }
    }

    /// <summary>All conflict files currently in the vault (for health reporting/alerting).</summary>
    public IReadOnlyList<string> ScanAll()
    {
        var results = new List<string>();
        Walk(new DirectoryInfo(resolver.Root), "");
        results.Sort(StringComparer.Ordinal);
        return results;

        void Walk(DirectoryInfo dir, string relBase)
        {
            foreach (var entry in dir.EnumerateFileSystemInfos())
            {
                if (entry.Name.StartsWith('.') || (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                var rel = relBase.Length == 0 ? entry.Name : relBase + '/' + entry.Name;
                if (entry is DirectoryInfo sub)
                    Walk(sub, rel);
                else if (entry.Name.Contains(Marker, StringComparison.Ordinal))
                    results.Add(rel);
            }
        }
    }
}
