using System.ComponentModel;
using Knapper.Core;
using Knapper.Core.Query;
using ModelContextProtocol.Server;

namespace Knapper.Mcp.Tools;

[McpServerToolType]
public sealed class VaultFilesTool(VaultFileLister lister, ToolSupport support)
{
    [McpServerTool(Name = "vault_files", UseStructuredContent = true, ReadOnly = true, OpenWorld = false)]
    [Description(
        "List vault files/directories with filters, sorted by path. Every response carries the completeness " +
        "envelope: truncated + nextCursor (pass cursor back to continue), scannedFiles, totalMatches, and the " +
        "vault generation span (changedDuringQuery=true means the vault moved while listing). " +
        "Hidden entries and control dirs (.git/.obsidian/.trash) are never visible.")]
    public QueryEnvelope<VaultFileEntry> Files(
        [Description("Directory to list under (vault-relative); omit for the whole vault")] string? pathPrefix = null,
        [Description("rg-style glob; without '/' it matches basenames at any depth (e.g. '*.md')")] string? glob = null,
        [Description("File extensions to include, e.g. [\"md\",\"sh\"]")] string[]? extensions = null,
        [Description("'all' (default), 'file', or 'directory'")] string? kind = null,
        [Description("Only entries modified strictly after this ISO-8601 instant")] DateTimeOffset? mtimeAfter = null,
        [Description("Only entries modified strictly before this ISO-8601 instant")] DateTimeOffset? mtimeBefore = null,
        [Description("Minimum file size in bytes (implies files only)")] long? minSize = null,
        [Description("Maximum file size in bytes (implies files only)")] long? maxSize = null,
        [Description("Include each file's SHA-256 (costs a read per file)")] bool includeSha = false,
        [Description("Page size (server-capped)")] int? maxResults = null,
        [Description("Continuation cursor from a previous truncated response")] string? cursor = null,
        CancellationToken ct = default) =>
        support.Run("vault_files", () => lister.List(new VaultFilesQuery
        {
            PathPrefix = pathPrefix,
            Glob = glob,
            Extensions = extensions,
            Kind = ParseKind(kind),
            MtimeAfter = mtimeAfter,
            MtimeBefore = mtimeBefore,
            MinSize = minSize,
            MaxSize = maxSize,
            IncludeSha = includeSha,
            MaxResults = maxResults,
            Cursor = cursor,
        }, ct));

    private static EntryKind ParseKind(string? kind) => kind?.ToLowerInvariant() switch
    {
        null or "" or "all" => EntryKind.All,
        "file" => EntryKind.File,
        "directory" or "dir" => EntryKind.Directory,
        _ => throw new KnapperException(VaultErrorCode.InvalidArgument,
            $"kind must be 'all', 'file', or 'directory', got '{kind}'"),
    };
}

[McpServerToolType]
public sealed class VaultSearchTool(VaultSearchService search, ToolSupport support)
{
    [McpServerTool(Name = "vault_search", UseStructuredContent = true, ReadOnly = true, OpenWorld = false)]
    [Description(
        "Full-text search over the vault (server-side ripgrep). Modes: 'matches' (records with path/line/column/" +
        "text and optional context), 'files' (paths containing a match), 'counts' (per-file match counts; the " +
        "envelope's totalMatches is the grand total). Responses wear the completeness envelope — truncated=false " +
        "means the scope was exhaustively searched; pass nextCursor back to continue a truncated page. " +
        "Column is a 1-based byte offset. Hidden files and control dirs are never searched.")]
    public object Search(
        [Description("The pattern (Rust regex syntax unless literal=true)")] string pattern,
        [Description("Treat pattern as a literal string, not a regex")] bool literal = false,
        [Description("'smart' (default: insensitive unless pattern has uppercase), 'sensitive', or 'insensitive'")] string? caseMode = null,
        [Description("Match whole words only")] bool wholeWord = false,
        [Description("Allow the pattern to span lines")] bool multiline = false,
        [Description("Vault-relative directory prefixes to scope the search (must not overlap)")] string[]? pathPrefixes = null,
        [Description("rg-style include globs (any may match)")] string[]? includeGlobs = null,
        [Description("rg-style exclude globs (exclusion wins)")] string[]? excludeGlobs = null,
        [Description("File extensions to include, e.g. [\"md\"]")] string[]? extensions = null,
        [Description("Context lines before each match (matches mode, max 50)")] int contextBefore = 0,
        [Description("Context lines after each match (matches mode, max 50)")] int contextAfter = 0,
        [Description("'matches' (default), 'files', or 'counts'")] string? mode = null,
        [Description("Page size (server-capped)")] int? maxResults = null,
        [Description("Continuation cursor from a previous truncated response")] string? cursor = null,
        CancellationToken ct = default) =>
        support.Run<object>("vault_search", () =>
        {
            var query = new VaultSearchQuery
            {
                Pattern = pattern,
                Literal = literal,
                Case = ParseCase(caseMode),
                WholeWord = wholeWord,
                Multiline = multiline,
                PathPrefixes = pathPrefixes,
                IncludeGlobs = includeGlobs,
                ExcludeGlobs = excludeGlobs,
                Extensions = extensions,
                ContextBefore = contextBefore,
                ContextAfter = contextAfter,
                MaxResults = maxResults,
                Cursor = cursor,
            };
            return mode?.ToLowerInvariant() switch
            {
                null or "" or "matches" => search.SearchMatches(query, ct),
                "files" => search.SearchFilesOnly(query with { Mode = SearchMode.FilesOnly }, ct),
                "counts" => search.SearchCounts(query with { Mode = SearchMode.Counts }, ct),
                _ => throw new KnapperException(VaultErrorCode.InvalidArgument,
                    $"mode must be 'matches', 'files', or 'counts', got '{mode}'"),
            };
        });

    private static CaseMode ParseCase(string? mode) => mode?.ToLowerInvariant() switch
    {
        null or "" or "smart" => CaseMode.Smart,
        "sensitive" => CaseMode.Sensitive,
        "insensitive" => CaseMode.Insensitive,
        _ => throw new KnapperException(VaultErrorCode.InvalidArgument,
            $"caseMode must be 'smart', 'sensitive', or 'insensitive', got '{mode}'"),
    };
}

[McpServerToolType]
public sealed class VaultFrontmatterTool(FrontmatterSearchService frontmatter, ToolSupport support)
{
    [McpServerTool(Name = "vault_search_frontmatter", UseStructuredContent = true, ReadOnly = true, OpenWorld = false)]
    [Description(
        "Query YAML frontmatter across .md notes: field existence, equality, or substring (both case-insensitive; " +
        "list fields match on any element). unparseableFiles lists notes whose frontmatter could not be examined " +
        "(broken YAML, non-UTF-8) — check it before trusting 'no match'.")]
    public FrontmatterSearchResult SearchFrontmatter(
        [Description("Top-level frontmatter field name")] string field,
        [Description("'exists' (default), 'equals', or 'contains'")] string? op = null,
        [Description("Value for equals/contains")] string? value = null,
        [Description("Directory to scope to (vault-relative)")] string? pathPrefix = null,
        [Description("Page size (server-capped)")] int? maxResults = null,
        [Description("Continuation cursor from a previous truncated response")] string? cursor = null,
        CancellationToken ct = default) =>
        support.Run("vault_search_frontmatter", () => frontmatter.Search(new FrontmatterQuery
        {
            Field = field,
            Op = op?.ToLowerInvariant() switch
            {
                null or "" or "exists" => FrontmatterOp.Exists,
                "equals" => FrontmatterOp.Equals,
                "contains" => FrontmatterOp.Contains,
                _ => throw new KnapperException(VaultErrorCode.InvalidArgument,
                    $"op must be 'exists', 'equals', or 'contains', got '{op}'"),
            },
            Value = value,
            PathPrefix = pathPrefix,
            MaxResults = maxResults,
            Cursor = cursor,
        }, ct));
}
