namespace Knapper.Core.Vault;

/// <summary>
/// Vault subtrees the operator has declared ARCHIVED: superseded copies kept
/// for history, which queries skip by default and which no mutation may
/// change once written.
///
/// <para><b>Why this is configuration and not a marker in the vault.</b> The
/// rule exists because agents do not reliably read the vault's own
/// <c>CLAUDE.md</c>. A rule stored IN the vault is editable by everything the
/// rule constrains — any agent (Knapper has no path exemptions) and anything
/// Sync delivers — so it could be switched off by the thing it was aimed at.
/// Configuration is the one channel an agent cannot reach.</para>
///
/// <para><b>Two rules, not one.</b> Query scoping and write protection are
/// separate decisions and this type answers them separately, because the
/// workflow that FILLS an archive is a write to it: a note is trimmed down
/// and its old version filed. So creating and moving INTO an archived prefix
/// stay legal (<see cref="Covers"/> is consulted for the SOURCE of a move and
/// for edits of things already there), while queries default to skipping the
/// whole subtree (<see cref="ExcludedFor"/>). A blanket write ban would have
/// banned archiving.</para>
///
/// <para><b>Matching is ordinal and boundary-aware</b> — never a bare
/// <c>StartsWith</c>. "Archive" must not also claim "Archived Recipes/", and
/// it must not claim "archive/": the vault filesystem is case-SENSITIVE by
/// hard requirement, ext4 legitimately distinguishes the two, and folding
/// here would silently hide a directory nobody excluded.</para>
///
/// <para>This never touches the filesystem and never combines its strings
/// with the vault root — it is a pure relative-path predicate, which is why
/// it does not go through <see cref="VaultPathResolver"/>. Prefixes name
/// directories that need not exist yet.</para>
/// </summary>
public sealed class ArchivedPrefixes
{
    public static readonly ArchivedPrefixes None = new([]);

    /// <summary>Normalized, sorted, de-duplicated. Empty means the feature is off.</summary>
    public IReadOnlyList<string> Prefixes { get; }

    public bool Any => Prefixes.Count > 0;

    public ArchivedPrefixes(IEnumerable<string>? configured)
    {
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in configured ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue; // an empty array element is how most config sources spell "none"
            seen.Add(Normalize(raw));
        }

        // A prefix under another prefix is redundant, not an error: both
        // exclude the same files. Dropping it keeps `excludedPrefixes` from
        // reporting the same subtree twice.
        Prefixes = [.. seen.Where(p => !seen.Any(other =>
            !string.Equals(other, p, StringComparison.Ordinal) && IsUnder(p, other)))];
    }

    /// <summary>
    /// Whether a vault-relative path lies in an archived subtree — the prefix
    /// directory itself included.
    /// </summary>
    public bool Covers(string relativePath) =>
        Prefixes.Any(p => IsUnder(relativePath, p));

    /// <summary>
    /// Which prefixes a query with this scope actually skips. Naming an
    /// archived prefix — or anything under one — is how a caller reaches
    /// archived content, so a query scoped there excludes NOTHING and must
    /// say so: reporting a skip that did not happen is as misleading as
    /// hiding one that did.
    ///
    /// <para>A caller may pass several scopes; a prefix is skipped only if no
    /// scope opts into it.</para>
    /// </summary>
    /// <returns>
    /// A NARROWED set, not a bare list, so the caller filters with the same
    /// <see cref="Covers"/> predicate it reports with. Handing back a
    /// <c>List&lt;string&gt;</c> invites a local <c>StartsWith</c> at the
    /// filter site, which is the boundary bug ("Archive" claiming "Archived
    /// Recipes") reintroduced one file away from the code that avoids it.
    /// </returns>
    public ArchivedPrefixes ExcludedFor(IEnumerable<string?>? queryScopes)
    {
        if (!Any)
            return None;

        var scopes = (queryScopes ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => Normalize(s!))
            .ToList();

        return new ArchivedPrefixes(Prefixes.Where(p => !scopes.Any(scope => IsUnder(scope, p))));
    }

    /// <summary>
    /// The same answer as <see cref="ExcludedFor(IEnumerable{string?})"/> for
    /// the single-scope surfaces (vault_files, vault_lint, frontmatter).
    /// </summary>
    public ArchivedPrefixes ExcludedFor(string? queryScope) => ExcludedFor([queryScope]);

    /// <summary>
    /// Path is the prefix itself, or lies beneath it. The separator check is
    /// what stops "Archive" from claiming "Archived Recipes".
    /// </summary>
    private static bool IsUnder(string relativePath, string prefix) =>
        string.Equals(relativePath, prefix, StringComparison.Ordinal)
        || relativePath.StartsWith(prefix + "/", StringComparison.Ordinal);

    /// <summary>
    /// Config is operator-supplied, so it is validated LOUDLY at boot rather
    /// than tolerated: a prefix that silently normalized to something else
    /// would protect a subtree nobody named.
    /// </summary>
    private static string Normalize(string raw)
    {
        var value = raw.Trim().Replace('\\', '/').Trim('/');
        if (value.Length == 0)
            throw Invalid(raw, "names the vault root — an archived prefix must name a subdirectory");
        if (Path.IsPathRooted(value))
            throw Invalid(raw, "is absolute — archived prefixes are vault-relative");
        foreach (var segment in value.Split('/'))
        {
            if (segment.Length == 0)
                throw Invalid(raw, "has an empty path segment");
            if (segment is "." or "..")
                throw Invalid(raw, "contains '.' or '..'");
            if (segment.StartsWith('.'))
                throw Invalid(raw, "is a dot-entry, which is already invisible and unaddressable");
        }
        return value;
    }

    private static KnapperException Invalid(string raw, string why) =>
        new(VaultErrorCode.InvalidPath,
            $"Vault:ArchivedPrefixes entry '{raw}' {why}");
}
