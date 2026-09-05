namespace Knapper.Mcp.Tools;

/// <summary>
/// The vault's note-writing conventions, spliced into the write tools'
/// descriptions.
///
/// WHY HERE. A tool description is the only channel that reaches an agent at
/// the moment it is drafting. Server instructions arrive once at initialize,
/// thousands of tokens before the write, and the vault's own CLAUDE.md
/// arrives only if the agent thought to read it — which is the problem this
/// exists to close. Descriptions arrive with tools/list and sit next to the
/// call being made.
///
/// WHY CONSTANTS. Attribute arguments must be compile-time constants, so
/// these compose into <c>[Description(...)]</c> by concatenation. One
/// definition, five tools: prose repeated by hand across five descriptions
/// drifts, and a convention that says two different things in two tool
/// descriptions is worse than one that is stated once.
///
/// ⚠️ SOURCE OF TRUTH IS THE VAULT'S OWN <c>CLAUDE.md</c>, not this file.
/// This is a deliberate EXCERPT of the rules that are (a) stable and (b)
/// actionable at the instant of a write — deploy-coupled on purpose, which is
/// only tolerable because these particular rules have not moved in a year.
/// Anything conditional, evolving, or merely descriptive (the folder map, the
/// per-type frontmatter table) does NOT belong here: it belongs in the
/// vaultConventions envelope field, which is served FROM the note and needs
/// no deploy. Adding a rule here means first asking whether it is stable
/// enough to be worth a release.
/// </summary>
internal static class VaultConventions
{
    /// <summary>How the content itself is written — applies to every write.</summary>
    public const string Writing =
        " CONVENTIONS: internal links are [[wikilinks]], never markdown links; do NOT add frontmatter to a " +
        "note that lacks it, or tags to a note that does not use them; match the vault's terse, " +
        "information-dense style — lists and tables over prose, with the specifics (model numbers, URLs, " +
        "dimensions) kept in.";

    /// <summary>
    /// What a query says about an archived subtree. Goes on the DISCOVERY
    /// surfaces, because the failure it prevents is an agent reading an
    /// exhaustive-looking empty result as "no such note" when the note is
    /// simply outside the default scope. The envelope's excludedPrefixes
    /// carries the same fact per response; this is what makes an agent expect
    /// it and know what to do about it.
    /// </summary>
    public const string ArchivedScope =
        " Archived subtrees are skipped by default; excludedPrefixes names them — scope to one to reach it.";

    /// <summary>
    /// The write-side half. An error an agent cannot act on becomes a retry
    /// loop or a workaround, so the clause says what IS allowed rather than
    /// only what is not.
    /// </summary>
    public const string ArchivedWrites =
        " [PathArchived]: the path is in an archived subtree. Creating and moving INTO one is allowed; " +
        "changing what is already there is a human action.";

    /// <summary>Where content goes — applies to the tools that choose a path.</summary>
    public const string Placement =
        " New notes default to Quicknotes/ unless a more specific folder clearly fits; the folder hierarchy " +
        "is not yours to reorganize.";
}
