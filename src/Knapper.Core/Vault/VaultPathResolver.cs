using Knapper.Core.Interop;

namespace Knapper.Core.Vault;

/// <summary>
/// The single gate between agent-supplied path strings and the filesystem.
/// Every tool argument that names a file goes through <see cref="Resolve"/>;
/// nothing else in the codebase may combine user input with the vault root.
///
/// <para>Rejections (all typed): absolute paths, `..` traversal, backslashes,
/// NUL, paths escaping the root, any symlink component inside the vault, the
/// control directories (.git/.obsidian/.trash), and Knapper's own hidden
/// temp files. Non-existent tails resolve fine — create needs that — and the
/// symlink walk stops at the first missing component (nothing deeper can
/// exist). A dangling symlink in the tail is invisible to the walk
/// (File/Directory.Exists follow links), which is safe: reads fail NotFound,
/// and the hard-link no-clobber create refuses to replace the link itself.</para>
/// </summary>
public sealed class VaultPathResolver
{
    private static readonly HashSet<string> BannedSegments =
        new(StringComparer.Ordinal) { ".git", ".obsidian", ".trash" };

    /// <summary>Canonical vault root: realpath-resolved, no trailing separator.</summary>
    public string Root { get; }

    public VaultPathResolver(string vaultRoot)
    {
        if (string.IsNullOrWhiteSpace(vaultRoot) || !Directory.Exists(vaultRoot))
            throw new KnapperException(VaultErrorCode.IoError, $"vault root does not exist: {vaultRoot}");
        Root = Posix.RealPath(vaultRoot);
    }

    public VaultPath Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw Invalid("path is empty");
        if (relativePath.Contains('\0'))
            throw Invalid("path contains NUL");
        if (relativePath.Contains('\\'))
            throw Invalid("backslash is not allowed in vault paths; use '/'");
        if (Path.IsPathRooted(relativePath))
            throw Invalid($"absolute paths are not accepted — pass a vault-relative path: {relativePath}");

        var segments = new List<string>();
        foreach (var raw in relativePath.Split('/'))
        {
            var segment = raw;
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == "..")
                throw Invalid($"path traversal ('..') is not allowed: {relativePath}");
            if (segment == "~" && segments.Count == 0)
                throw Invalid($"'~' is not expanded here — pass a vault-relative path: {relativePath}");
            if (BannedSegments.Contains(segment))
                throw new KnapperException(VaultErrorCode.BannedPath,
                    $"'{segment}/' is a control directory and is never accessible through this service: {relativePath}");
            if (segment.StartsWith(AtomicFile.TempPrefix, StringComparison.Ordinal))
                throw new KnapperException(VaultErrorCode.BannedPath,
                    $"Knapper temp files are not addressable: {relativePath}");
            segments.Add(segment);
        }

        if (segments.Count == 0)
            throw Invalid($"path resolves to the vault root itself: {relativePath}");

        var relative = string.Join('/', segments);
        var absolute = Root + Path.DirectorySeparatorChar + relative;

        // Belt and suspenders: the segment rules above already make escape
        // impossible, but the containment property is the one we never want
        // to depend on a single check for.
        var full = Path.GetFullPath(absolute);
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new KnapperException(VaultErrorCode.PathOutsideVault, $"path escapes the vault: {relativePath}");

        RejectSymlinkComponents(segments, relativePath);

        return new VaultPath { Relative = relative, Absolute = full };
    }

    private void RejectSymlinkComponents(List<string> segments, string original)
    {
        var current = Root;
        foreach (var segment in segments)
        {
            current = current + Path.DirectorySeparatorChar + segment;
            if (!File.Exists(current) && !Directory.Exists(current))
                return; // nothing deeper can exist either
            if (File.ResolveLinkTarget(current, returnFinalTarget: false) is not null)
                throw new KnapperException(VaultErrorCode.SymlinkRejected,
                    $"refusing symlink path component '{segment}': {original}");
        }
    }

    private static KnapperException Invalid(string message) =>
        new(VaultErrorCode.InvalidPath, message);
}
