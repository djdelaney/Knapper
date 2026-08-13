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
    /// Vault-relative paths over <paramref name="limitBytes"/>, ordinal-sorted.
    /// Throws <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>
    /// on a failed walk rather than returning a short list — a partial scan
    /// reported as "none found" is the same lie as an empty search claiming
    /// exhaustive coverage. Callers decide what to do with "could not tell".
    ///
    /// Dot-entries are skipped at every depth, matching what queries can see:
    /// .git packfiles and .obsidian plugin bundles routinely exceed the
    /// ceiling, none of them sync, and listing them would be permanent noise
    /// that trains the reader to ignore the warning.
    /// </summary>
    public static IReadOnlyList<string> Scan(string root, long limitBytes)
    {
        var found = new List<string>();
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            foreach (var entry in new DirectoryInfo(stack.Pop()).EnumerateFileSystemInfos())
            {
                if (entry.Name.StartsWith('.'))
                    continue;
                if (entry is DirectoryInfo dir)
                    stack.Push(dir.FullName);
                else if (entry is FileInfo file && file.Length > limitBytes)
                    found.Add(Path.GetRelativePath(root, file.FullName));
            }
        }
        found.Sort(StringComparer.Ordinal);
        return found;
    }
}
