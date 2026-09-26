namespace Knapper.Core.Options;

/// <summary>
/// The deployed vault's note-writing conventions: what the write tools'
/// descriptions state, and which of them a write is checked against.
///
/// <para>CONFIGURATION, never vault content, for the reason
/// <see cref="VaultOptions.ArchivedPrefixes"/> is: these rules constrain
/// agents, and an agent can write any vault path — a rule read from a note
/// could be switched off by the thing it is aimed at. The vault's own
/// CLAUDE.md stays what it is (guidance an agent reads); nothing here parses
/// it.</para>
///
/// <para>Every field defaults OFF. The build ships no one's conventions: a
/// deployment states its own, and one that sets nothing publishes write-tool
/// descriptions carrying no convention text at all. Each rule flag drives
/// BOTH the sentence in the descriptions and the matching write warning, so
/// the two cannot disagree about what the vault expects.</para>
/// </summary>
public sealed class ConventionsOptions
{
    public const string SectionName = "Conventions";

    /// <summary>
    /// Upper bound on each free-text field. The composed description must
    /// stay under the client's 2048-character delivery cap (startup refuses
    /// one that does not); this keeps a single field from being the reason.
    /// </summary>
    public const int MaxTextLength = 400;

    /// <summary>
    /// Internal links are <c>[[wikilinks]]</c>, never markdown links. Warns
    /// when a write ADDS a markdown link or embed whose target is not a URL.
    /// </summary>
    public bool WikilinksOnly { get; set; }

    /// <summary>
    /// Do not add frontmatter to a note that lacks it. Warns when an edit or
    /// append gives an existing note a frontmatter block it did not have. A
    /// NEW note is not checked: whether a note type carries frontmatter is a
    /// per-vault rule a flag cannot express.
    /// </summary>
    public bool NoNewFrontmatter { get; set; }

    /// <summary>
    /// Do not add tags to a note that does not use them. Warns when an edit or
    /// append gives an existing tagless note its first tag (inline
    /// <c>#tag</c> or a frontmatter <c>tags</c> key). New notes are not
    /// checked, for the reason given on <see cref="NoNewFrontmatter"/>.
    /// </summary>
    public bool NoNewTags { get; set; }

    /// <summary>
    /// Free-text style guidance appended to the writing clause, e.g. "match
    /// the vault's terse, information-dense style". Stated, never checked.
    /// </summary>
    public string? Style { get; set; }

    /// <summary>
    /// Vault-relative folder new notes default to (e.g. <c>Quicknotes</c>),
    /// stated on the tools that choose a path. Stated, never checked: "unless
    /// a more specific folder clearly fits" is a judgement, not a rule.
    /// </summary>
    public string? NewNoteFolder { get; set; }

    /// <summary>True when any rule is checked on write — the descriptions then say so.</summary>
    public bool AnyChecked => WikilinksOnly || NoNewFrontmatter || NoNewTags;

    /// <summary>
    /// Refuse a malformed configuration at startup rather than publishing it.
    /// Returns the problems; empty means valid.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        CheckText(nameof(Style), Style, problems);
        CheckText(nameof(NewNoteFolder), NewNoteFolder, problems);
        if (NewNoteFolder is { } folder && (folder.StartsWith('/') || folder.Split('/').Any(s => s is "." or "..")))
            problems.Add($"Conventions:{nameof(NewNoteFolder)} must be vault-relative with no '.' or '..' segments, got '{folder}'");
        return problems;
    }

    private static void CheckText(string name, string? value, List<string> problems)
    {
        if (value is null)
            return;
        if (value.Length > MaxTextLength)
            problems.Add($"Conventions:{name} is {value.Length} characters; the cap is {MaxTextLength}");
        if (value.Any(char.IsControl))
            problems.Add($"Conventions:{name} contains a control character (newlines included); it is spliced into one-line prose");
    }
}
