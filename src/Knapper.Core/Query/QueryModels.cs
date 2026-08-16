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

public sealed record QueryEnvelope<T>(
    IReadOnlyList<T> Items,
    bool Truncated,
    string? NextCursor,
    int? ScannedFiles,
    int ReturnedItems,
    long? TotalMatches,
    long GenerationStart,
    long GenerationEnd,
    bool ChangedDuringQuery) : IFreshnessSignals
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
            TotalMatches, GenerationStart, GenerationEnd, ChangedDuringQuery);
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
public sealed record FrontmatterSearchResult(
    QueryEnvelope<FrontmatterMatch> Envelope,
    IReadOnlyList<string> UnparseableFiles) : IFreshnessSignals
{
    bool IFreshnessSignals.WasTruncated => Envelope.Truncated;
    bool IFreshnessSignals.MovedDuringQuery => Envelope.ChangedDuringQuery;
}
