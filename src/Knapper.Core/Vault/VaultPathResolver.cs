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
/// exist). A DANGLING symlink is rejected like any other: the walk asks
/// whether the component is a link, not whether it resolves.</para>
/// </summary>
public sealed class VaultPathResolver
{
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
            if (segment.StartsWith('.'))
            {
                // Hidden means invisible on BOTH surfaces — and unaddressable
                // on this one. Queries never enumerate dot-entries, so an
                // agent that could still create/read/edit them would operate
                // where nothing can observe it (and a synced .env would be
                // readable by direct addressing). This ONE check subsumes the
                // control dirs (.git/.obsidian/.trash) and Knapper temp
                // files — they are all dot-prefixed; do not re-add separate
                // checks "for sharper messages", they'd be dead code.
                throw new KnapperException(VaultErrorCode.BannedPath,
                    $"hidden path segment '{segment}' — dot-entries (including .git/.obsidian/.trash and " +
                    "Knapper temp files) are invisible to queries and unaddressable by design");
            }
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

    private void RejectSymlinkComponents(List<string> segments, string original) =>
        RejectSymlinkComponents(Root, segments, original);

    /// <summary>
    /// The symlink rule, stated once. <c>Resolve</c> applies it to every
    /// agent-supplied path; <c>VaultMutationService</c> applies it to the
    /// <c>.trash/</c> chain it assembles itself — a path no agent can name
    /// (dot segments are unaddressable) and which therefore never passes
    /// through <c>Resolve</c>, but which still ends in <c>link(2)</c> against
    /// a directory chain a human could have replaced with a symlink. Two
    /// copies of this walk would be two chances to disagree about what
    /// "inside the vault" means.
    /// </summary>
    internal static void RejectSymlinkComponents(string root, IReadOnlyList<string> segments, string original)
    {
        var current = root;
        foreach (var segment in segments)
        {
            current = current + Path.DirectorySeparatorChar + segment;
            // FileInfo.LinkTarget, not Exists-then-ResolveLinkTarget: it is
            // non-null for EVERY symlink including a dangling one, null for a
            // missing path, and throws for neither — so the answer does not
            // ride on whether File.Exists follows links on this runtime and
            // platform. ResolveLinkTarget throws on a missing path, which is
            // what forced the existence check to come first and made a
            // dangling link depend on that detail.
            if (new FileInfo(current).LinkTarget is not null)
            {
                throw new KnapperException(VaultErrorCode.SymlinkRejected,
                    $"refusing symlink path component '{segment}': {original}");
            }
            if (!File.Exists(current) && !Directory.Exists(current))
                return; // nothing deeper can exist either
        }
    }

    private static KnapperException Invalid(string message) =>
        new(VaultErrorCode.InvalidPath, message);
}
