using Knapper.Core.Interop;

namespace Knapper.Core.Vault;

/// <summary>
/// Containment answers for the startup fail-closed checks ("is the lock dir /
/// audit path inside the vault?"). Lexical prefix comparison is not enough:
/// it misses a path EQUAL to the vault root, and it misses a path whose
/// ancestor is a symlink back into the vault — both would let operational
/// files land inside the synced tree. Canonicalization resolves symlinks via
/// realpath(3) on the deepest existing ancestor (the tail may not exist yet
/// at startup — lock dirs and audit files are created later).
/// </summary>
public static class PathContainment
{
    /// <summary>
    /// True when <paramref name="candidate"/> canonicalizes to the vault root
    /// itself or to anything beneath it.
    /// </summary>
    public static bool IsInsideOrEqual(string candidate, string vaultRoot)
    {
        var root = Canonicalize(vaultRoot);
        var resolved = Canonicalize(candidate);
        return resolved == root || resolved.StartsWith(root + '/', StringComparison.Ordinal);
    }

    /// <summary>
    /// Full path with every symlink in the EXISTING prefix resolved; the
    /// not-yet-existing tail is re-appended lexically (there is nothing to
    /// resolve for components that don't exist yet).
    /// </summary>
    public static string Canonicalize(string path)
    {
        var current = Path.GetFullPath(path);
        var tail = new List<string>();
        while (!Directory.Exists(current) && !File.Exists(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
                break; // fell off the root — nothing existing to resolve
            tail.Add(Path.GetFileName(current));
            current = parent;
        }
        if (Directory.Exists(current) || File.Exists(current))
            current = Posix.RealPath(current);
        for (var i = tail.Count - 1; i >= 0; i--)
            current = Path.Combine(current, tail[i]);
        return current;
    }
}
