using System.Text.Json.Serialization;

namespace Knapper.Core.Query;

/// <summary>
/// The completeness envelope every list/search response wears (brief §6).
/// <c>Truncated=false</c> is a strong claim: the scope was exhaustively
/// searched and nothing was withheld. A cap is acceptable only as
/// <c>Truncated=true</c> plus a usable cursor. <c>TotalMatches</c> is the
/// full match count across ALL pages when known, and explicitly null when
/// not — never guessed.
/// </summary>
/// <summary>
/// Completeness/freshness bits every query/read result exposes uniformly so
/// the MCP layer can feed the metrics surface (brief §8: truncation and
/// generation-changed rates) without knowing each concrete result shape.
/// </summary>
public interface IFreshnessSignals
{
    bool WasTruncated { get; }
    bool MovedDuringQuery { get; }
}

/// <remarks>
/// Unsealed for exactly one reason: a query surface that carries an EXTRA
/// field alongside the envelope (frontmatter's <c>unparseableFiles</c>)
/// inherits this record rather than holding one as a member. Holding one
/// nests it on the wire — clients then need a special-case parser for that
/// one tool — and duplicating the nine fields instead invites drift, since
/// nothing would force a newly added envelope field into the copy. Deriving
/// gets both: flat JSON, and a compile error here the moment the envelope
/// grows. Derive only to ADD fields; never to change what one means.
/// </remarks>
public record QueryEnvelope<T>(
    IReadOnlyList<T> Items,
    bool Truncated,
    string? NextCursor,
    int? ScannedFiles,
    int ReturnedItems,
    long? TotalMatches,
    long GenerationStart,
    long GenerationEnd,
    bool ChangedDuringQuery,
    /// <summary>
    /// Archived subtrees (<c>Vault:ArchivedPrefixes</c>) this query did NOT
    /// look in — empty list when it searched everything, never omitted.
    ///
    /// <para>This field is what keeps <c>Truncated == false</c> honest. That
    /// flag means "the scope was searched exhaustively", and a server-side
    /// exclusion narrows the scope; declaring which prefixes were skipped
    /// makes the narrowing VISIBLE, so an agent that finds nothing can tell
    /// "it does not exist" from "it may be in a subtree I did not look in,
    /// and here is its name". An undeclared exclusion would be the same
    /// defect as the >5MB files Sync never delivers, except firing on every
    /// query instead of rarely: the difference between the two is that this
    /// absence is KNOWN to the server, so there is no excuse for it to be
    /// silent.</para>
    ///
    /// <para>Naming an archived prefix as a query scope reaches it, and then
    /// this list is empty for that prefix — a skip that did not happen must
    /// not be reported.</para>
    /// </summary>
    IReadOnlyList<string> ExcludedPrefixes) : IFreshnessSignals
{
    bool IFreshnessSignals.WasTruncated => Truncated;
    bool IFreshnessSignals.MovedDuringQuery => ChangedDuringQuery;

    /// <summary>
    /// Project the items, carrying every completeness/freshness field through
    /// untouched. Field-by-field re-construction at a call site is how a
    /// truncation flag or a generation bound gets silently dropped.
    /// </summary>
    public QueryEnvelope<TOut> Map<TOut>(Func<T, TOut> project) =>
        new([.. Items.Select(project)], Truncated, NextCursor, ScannedFiles, ReturnedItems,
            TotalMatches, GenerationStart, GenerationEnd, ChangedDuringQuery, ExcludedPrefixes);
}

public enum CaseMode
{
    /// <summary>Case-insensitive unless the pattern contains an uppercase letter (rg -S).</summary>
    Smart,
    Sensitive,
    Insensitive,
}

public enum SearchMode
{
    /// <summary>Full match records with line/column/context.</summary>
    Matches,
    /// <summary>Paths of files containing at least one match.</summary>
    FilesOnly,
    /// <summary>Per-file match counts; the envelope's TotalMatches carries the sum.</summary>
    Counts,
}

public sealed record VaultSearchQuery
{
    public required string Pattern { get; init; }
    /// <summary>Treat the pattern as a literal string, not a regex.</summary>
    public bool Literal { get; init; }
    public CaseMode Case { get; init; } = CaseMode.Smart;
    public bool WholeWord { get; init; }
    /// <summary>Allow matches to span lines (rg -U).</summary>
    public bool Multiline { get; init; }
    /// <summary>Vault-relative directory prefixes to scope the search. Must not overlap.</summary>
    public IReadOnlyList<string>? PathPrefixes { get; init; }
    /// <summary>rg-style include globs (any may match).</summary>
    public IReadOnlyList<string>? IncludeGlobs { get; init; }
    /// <summary>rg-style exclude globs (exclusion wins).</summary>
    public IReadOnlyList<string>? ExcludeGlobs { get; init; }
    /// <summary>File extensions (with or without leading dot) — sugar for include globs.</summary>
    public IReadOnlyList<string>? Extensions { get; init; }
    public int ContextBefore { get; init; }
    public int ContextAfter { get; init; }
    public SearchMode Mode { get; init; } = SearchMode.Matches;
    /// <summary>Page size; clamped to VaultOptions.MaxResultsPerPage.</summary>
    public int? MaxResults { get; init; }
    public string? Cursor { get; init; }
}

/// <summary>
/// One match record. One record per submatch: a line containing the pattern
/// twice yields two records (aligning TotalMatches with rg --count-matches).
/// <c>Column</c> is a 1-based BYTE offset within the line (rg convention).
/// For multiline matches <c>Text</c> spans the matched lines and
/// <c>Line</c> is the first.
/// </summary>
public sealed record SearchMatch(
    string Path,
    int Line,
    int Column,
    string Text,
    IReadOnlyList<string>? ContextBefore,
    IReadOnlyList<string>? ContextAfter);

public sealed record FileMatchCount(string Path, long Count);

/// <summary>
/// The ONE wire shape vault_search returns, across all three modes. The three
/// modes produce genuinely different records (match / bare path / per-file
/// count), and the obvious C# spelling of that — a tool method returning
/// <c>object</c> — publishes an outputSchema of bare <c>true</c>, which
/// strict MCP clients reject hard enough to discard the entire tool list.
/// See <see cref="Knapper.Core.ToolSchemaContract"/>.
///
/// So the union is expressed in the DATA instead, where a schema can describe
/// it: <c>Path</c> is always present, and which of the rest are populated
/// follows the mode — matches fills line/column/text (+ context when asked),
/// counts fills count, files fills neither. Nulls are omitted on the wire, so
/// a files-mode record is <c>{"path": "…"}</c> and nothing more.
/// </summary>
public sealed record SearchResultItem(
    string Path,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Line = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Column = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ContextBefore = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? ContextAfter = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Count = null)
{
    public static SearchResultItem FromMatch(SearchMatch match) =>
        new(match.Path, match.Line, match.Column, match.Text, match.ContextBefore, match.ContextAfter);

    public static SearchResultItem FromPath(string path) => new(path);

    public static SearchResultItem FromCount(FileMatchCount count) => new(count.Path, Count: count.Count);
}

public enum EntryKind
{
    All,
    File,
    Directory,
}

public sealed record VaultFilesQuery
{
    /// <summary>Vault-relative directory to list under; null/empty = whole vault.</summary>
    public string? PathPrefix { get; init; }
    /// <summary>rg-style glob: no '/' matches basenames at any depth, with '/' matches the full relative path.</summary>
    public string? Glob { get; init; }
    public IReadOnlyList<string>? Extensions { get; init; }
    public EntryKind Kind { get; init; } = EntryKind.All;
    public DateTimeOffset? MtimeAfter { get; init; }
    public DateTimeOffset? MtimeBefore { get; init; }
    /// <summary>Size filters imply files: directories have no size and are excluded when either is set.</summary>
    public long? MinSize { get; init; }
    public long? MaxSize { get; init; }
    /// <summary>Compute SHA-256 per file (costs a full read of each returned file).</summary>
    public bool IncludeSha { get; init; }
    public int? MaxResults { get; init; }
    public string? Cursor { get; init; }
}

public sealed record VaultFileEntry(
    string Path,
    bool IsDirectory,
    long? Size,
    DateTimeOffset Mtime,
    string? Sha256);

public sealed record VaultReadResult(
    string Path,
    string Content,
    /// <summary>SHA-256 of the WHOLE file's raw bytes — the mutation precondition — even for ranged reads.</summary>
    string Sha256,
    long Size,
    DateTimeOffset Mtime,
    string Encoding,
    int TotalLines,
    int? RangeStart,
    int? RangeEnd,
    /// <summary>Freshness signal only (brief §6) — the SHA remains the mutation precondition.</summary>
    long GenerationStart,
    long GenerationEnd,
    bool ChangedDuringRead) : IFreshnessSignals
{
    bool IFreshnessSignals.WasTruncated => false; // reads are never truncated — TooLarge is a typed refusal
    bool IFreshnessSignals.MovedDuringQuery => ChangedDuringRead;
}

public sealed record VaultBatchReadItem(
    string Path,
    VaultReadResult? Result,
    VaultErrorCode? ErrorCode,
    string? ErrorMessage);

/// <summary>
/// Batch envelope: the span brackets the WHOLE batch, while each successful
/// item's embedded result carries its own per-file span. Changed=true means
/// the vault moved while the batch was being read — items read before the
/// change may be mutually inconsistent with items read after it.
/// </summary>
public sealed record VaultBatchReadResult(
    IReadOnlyList<VaultBatchReadItem> Items,
    long GenerationStart,
    long GenerationEnd,
    bool ChangedDuringRead) : IFreshnessSignals
{
    bool IFreshnessSignals.WasTruncated => false;
    bool IFreshnessSignals.MovedDuringQuery => ChangedDuringRead;
}

public sealed record VaultReadRequest(string Path, int? StartLine = null, int? EndLine = null);

public sealed record VaultStatResult(
    string Path,
    bool Exists,
    bool IsDirectory,
    long? Size,
    DateTimeOffset? Mtime,
    /// <summary>"utf-8", "utf-8-bom", or "binary". Null for directories/missing.</summary>
    string? Encoding,
    bool? IsText,
    /// <summary>
    /// Null for directories and missing files ONLY. Files beyond MaxReadBytes
    /// still hash (streamed): the SHA is the move/soft-delete precondition,
    /// and omitting it would strand large synced attachments. Their
    /// TotalLines is null and text detection is bounded to a prefix.
    /// </summary>
    string? Sha256,
    int? TotalLines,
    /// <summary>Freshness signal only — the SHA remains the mutation precondition.</summary>
    long GenerationStart,
    long GenerationEnd,
    bool ChangedDuringRead) : IFreshnessSignals
{
    bool IFreshnessSignals.WasTruncated => false;
    bool IFreshnessSignals.MovedDuringQuery => ChangedDuringRead;
}

public enum FrontmatterOp
{
    Exists,
    /// <summary>Field (or any list element) equals the value, case-insensitively.</summary>
    Equals,
    /// <summary>Field (or any list element) contains the value as a substring, case-insensitively.</summary>
    Contains,
}

public sealed record FrontmatterQuery
{
    public required string Field { get; init; }
    public FrontmatterOp Op { get; init; } = FrontmatterOp.Exists;
    public string? Value { get; init; }
    public string? PathPrefix { get; init; }
    public int? MaxResults { get; init; }
    public string? Cursor { get; init; }
}

public sealed record FrontmatterMatch(string Path, string Field, string? Value);

/// <summary>
/// Frontmatter results carry the files whose frontmatter would not parse —
/// a file with broken YAML could otherwise hide a match silently, and
/// "no match" must mean the scope was exhaustively searched.
/// </summary>
/// <summary>
/// The completeness envelope, FLAT, plus the files this search could not
/// examine. Every query surface answers with the envelope at the top level —
/// this one nested it under an <c>envelope</c> key until 0.5.0, which forced
/// a client-side result parser to special-case one tool out of thirteen.
/// </summary>
public sealed record FrontmatterSearchResult(
    IReadOnlyList<FrontmatterMatch> Items,
    bool Truncated,
    string? NextCursor,
    int? ScannedFiles,
    int ReturnedItems,
    long? TotalMatches,
    long GenerationStart,
    long GenerationEnd,
    bool ChangedDuringQuery,
    /// <summary>
    /// Notes whose frontmatter could not be examined (broken YAML, non-UTF-8).
    /// A skipped file could be hiding a match, so "no match" is only
    /// exhaustive once this is empty — never omitted, empty list when clean.
    /// </summary>
    IReadOnlyList<string> UnparseableFiles,
    IReadOnlyList<string> ExcludedPrefixes)
    : QueryEnvelope<FrontmatterMatch>(
        Items, Truncated, NextCursor, ScannedFiles, ReturnedItems, TotalMatches,
        GenerationStart, GenerationEnd, ChangedDuringQuery, ExcludedPrefixes);

/// <summary>
/// The lint check names, as clients see them. These are wire strings, not a
/// C# enum, for the same reason tool names live in <c>ToolNames</c>: the
/// baseline design (proposal §5) keys a finding on
/// <c>(check, path, subject)</c> and compares that key across two vault
/// trees, so a check name is a durable identifier a later release must not
/// silently respell. Adding a check is additive; renaming one is a version
/// bump.
/// </summary>
public static class LintChecks
{
    /// <summary>A [[link]] whose target matches no vault file.</summary>
    public const string UnresolvedLink = "unresolved_link";
    /// <summary>A basename-form [[link]] matching two or more files. Obsidian silently picks one.</summary>
    public const string AmbiguousLink = "ambiguous_link";
    /// <summary>A resolved link whose #heading or #^block does not exist in the target.</summary>
    public const string BrokenAnchor = "broken_anchor";
    /// <summary>An unescaped '|' inside a wikilink inside a table row — it opens a phantom column.</summary>
    public const string TablePipe = "table_pipe";
    /// <summary>A table with no blank line above it: Obsidian renders it as paragraph text, pipes and all.</summary>
    public const string TableNeedsBlankLine = "table_needs_blank_line";

    public static readonly IReadOnlyList<string> All =
        [UnresolvedLink, AmbiguousLink, BrokenAnchor, TablePipe, TableNeedsBlankLine];
}

public sealed record LintQuery
{
    /// <summary>
    /// Vault-relative directory whose files are REPORTED on. The link index
    /// is always whole-vault regardless — a link inside the scope can point
    /// anywhere, so a scoped index would manufacture unresolved findings.
    /// </summary>
    public string? PathPrefix { get; init; }
    /// <summary>Check names to run; null or empty runs them all.</summary>
    public IReadOnlyList<string>? Checks { get; init; }
    public int? MaxResults { get; init; }
    public string? Cursor { get; init; }
}

/// <summary>
/// One observation. Not a work item: proposal §7 is explicit that a cluster
/// of related findings usually means a decision about intent rather than an
/// edit, and this vault is the worked example — accidentally bracketed
/// plain-text names ([[La-Z-Boy]]) are correctly unresolved and are fixed by
/// UNbracketing, not by creating a note.
///
/// <c>Subject</c> is the stable identity: the normalized link target with its
/// fragment, the raw link text for a table_pipe finding, or the header row
/// for a table_needs_blank_line one. <c>Line</c> and
/// <c>Column</c> are INFORMATIONAL — a paragraph inserted above a finding
/// moves both, and §5's baseline is keyed on (check, path, subject) precisely
/// so that edit does not read as a flood of new findings.
/// </summary>
public sealed record LintFinding(
    string Check,
    string Path,
    string Subject,
    int Line,
    int Column,
    string Message);

/// <summary>
/// The completeness envelope, FLAT, plus the notes this lint could not read.
/// The extra field is not decoration: an unreadable note is still a valid
/// LINK TARGET (the file exists), but its headings are unknown, so anchor
/// findings against it are suppressed rather than guessed. "No findings"
/// is only exhaustive once this list is empty.
/// </summary>
public sealed record LintResult(
    IReadOnlyList<LintFinding> Items,
    bool Truncated,
    string? NextCursor,
    int? ScannedFiles,
    int ReturnedItems,
    long? TotalMatches,
    long GenerationStart,
    long GenerationEnd,
    bool ChangedDuringQuery,
    IReadOnlyList<string> UnexaminedFiles,
    IReadOnlyList<string> ExcludedPrefixes)
    : QueryEnvelope<LintFinding>(
        Items, Truncated, NextCursor, ScannedFiles, ReturnedItems, TotalMatches,
        GenerationStart, GenerationEnd, ChangedDuringQuery, ExcludedPrefixes);
