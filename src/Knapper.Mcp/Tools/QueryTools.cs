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
        "envelope: truncated + nextCursor (pass cursor back to continue), scannedFiles, totalMatches (here the " +
        "count of matching entries across ALL pages, known on every page — this listing walks the whole scope " +
        "before paginating), and the " +
        "vault generation span (changedDuringQuery=true means the vault moved while listing). " +
        "Hidden entries and control dirs (.git/.obsidian/.trash) are never visible." +
        VaultConventions.ArchivedScope)]
    public QueryEnvelope<VaultFileEntry> Files(
        [Description("Directory to list under (vault-relative); omit for the whole vault")] string? pathPrefix = null,
        [Description("rg-style glob; without '/' it matches basenames at any depth (e.g. '*.md'). " +
            "Include-only: a leading '!' is REFUSED, not exclusion — narrow positively, or use " +
            "vault_search's exclude_globs.")] string? glob = null,
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
        "text and optional context), 'files' (paths containing a match), 'counts' (per-file match counts). " +
        "Responses wear the completeness envelope — truncated=false " +
        "means the scope was exhaustively searched; pass nextCursor back to continue a truncated page. " +
        "totalMatches is a match count across the WHOLE scope and is null whenever this search could not " +
        "establish one: always in files mode (which counts files, not matches — use vault_files for a total " +
        "entry count), and on any page that was cut short by the page size or the time budget. It is populated " +
        "in counts mode on a page that ran to completion, including the last page of a paginated search. " +
        "A null is never a zero. " +
        "Column is a 1-based byte offset. Hidden files and control dirs are never searched. Every mode returns " +
        "the same item shape: 'path' always, plus line/column/text (+context) in matches mode and 'count' in " +
        "counts mode; fields the mode does not fill are omitted." + VaultConventions.ArchivedScope)]
    public QueryEnvelope<SearchResultItem> Search(
        [Description("The pattern (Rust regex syntax unless literal=true)")] string pattern,
        [Description("Treat pattern as a literal string, not a regex")] bool literal = false,
        [Description("'smart' (default: insensitive unless pattern has uppercase), 'sensitive', or 'insensitive'")] string? caseMode = null,
        [Description("Match whole words only")] bool wholeWord = false,
        [Description("Allow the pattern to span lines")] bool multiline = false,
        [Description("Vault-relative directory prefixes to scope the search (must not overlap)")] string[]? pathPrefixes = null,
        [Description("rg-style include globs (any may match). A leading '!' is REFUSED — exclusion " +
            "goes in exclude_globs, without the '!'.")] string[]? includeGlobs = null,
        [Description("rg-style exclude globs (exclusion wins). Pass the pattern WITHOUT a leading " +
            "'!' — the exclusion is this parameter; a '!' here is REFUSED.")] string[]? excludeGlobs = null,
        [Description("File extensions to include, e.g. [\"md\"]")] string[]? extensions = null,
        [Description("Context lines before each match (matches mode, max 50)")] int contextBefore = 0,
        [Description("Context lines after each match (matches mode, max 50)")] int contextAfter = 0,
        [Description("'matches' (default), 'files', or 'counts'")] string? mode = null,
        [Description("Page size (server-capped)")] int? maxResults = null,
        [Description("Continuation cursor from a previous truncated response")] string? cursor = null,
        CancellationToken ct = default) =>
        // The declared return type is load-bearing, not decoration: an
        // 'object' here (the natural spelling of a three-shape union) makes
        // the SDK publish outputSchema = true, which strict clients reject —
        // taking the whole tool list down with it. Every mode is projected
        // onto the ONE concrete item type a schema can describe.
        support.Run("vault_search", () =>
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
                null or "" or "matches" =>
                    search.SearchMatches(query, ct).Map(SearchResultItem.FromMatch),
                "files" =>
                    search.SearchFilesOnly(query with { Mode = SearchMode.FilesOnly }, ct)
                        .Map(SearchResultItem.FromPath),
                "counts" =>
                    search.SearchCounts(query with { Mode = SearchMode.Counts }, ct)
                        .Map(SearchResultItem.FromCount),
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
        "(broken YAML, non-UTF-8) — check it before trusting 'no match'." +
        VaultConventions.ArchivedScope)]
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

[McpServerToolType]
public sealed class VaultLintTool(VaultLintService lint, ToolSupport support)
{
    [McpServerTool(Name = "vault_lint", UseStructuredContent = true, ReadOnly = true, OpenWorld = false)]
    // Clients cut this at ToolSchemaContract.ClientTextBudget's measured 2048
    // characters, silently. At 2185 it arrived ending mid-word — "inserting a
    // paragraph moves bot" — so the agent never saw the two sentences that
    // stop it treating a whole-vault run as a list of what recently broke.
    // The cut always takes the TAIL, which is why the two rules an agent must
    // not miss (findings are observations; never bulk-fix) sit near the
    // front, ahead of every mechanical detail.
    [Description(
        "Read-only consistency checks over the vault's link graph. FINDINGS ARE OBSERVATIONS FOR THE USER, NOT A " +
        "WORK LIST: fixing them is not implied by finding them, and a cluster usually means ONE decision about " +
        "intent rather than a series of edits — an unresolved [[Some Brand Name]] is usually plain text that was " +
        "accidentally bracketed, and a stale #heading is often one renamed heading with many inbound links. Report " +
        "what you find and ask; never bulk-fix. Findings have no baseline yet, so a whole-vault run reports " +
        "the standing backlog, not what changed recently. " +
        "Checks: 'unresolved_link' (a [[link]] matching no vault file), 'ambiguous_link' (a bare basename matching " +
        "two or more notes where neither exact case nor proximity settles it — Obsidian silently picks one), " +
        "'broken_anchor' (a #heading or #^block that does not exist in the resolved target), 'table_pipe' (an " +
        "unescaped '|' inside a wikilink inside a table row, which opens a column the author did not intend), " +
        "'table_needs_blank_line' (a table whose header row has no blank line above it — Obsidian absorbs it into " +
        "the paragraph or bullet above, at any indent, and renders every row as literal text; the fix is one blank " +
        "line at the table's own indent. A table under a HEADING, or inside a code block, is fine). " +
        "Scope: pathPrefix limits which files are REPORTED on; the link index is always whole-vault, because a " +
        "link inside the scope can point anywhere. Embeds (![[...]]) are not checked. " +
        "Responses wear the completeness envelope; unexaminedFiles lists notes that could not be read — still " +
        "valid link TARGETS with unknown headings, so anchor findings against them are suppressed rather than " +
        "guessed, and 'no findings' is exhaustive only once that list is empty. line/column locate a finding but " +
        "are not its identity." + VaultConventions.ArchivedScope)]
    public LintResult Lint(
        [Description("Directory whose files are reported on (vault-relative); omit for the whole vault")] string? pathPrefix = null,
        [Description("Checks to run, e.g. [\"broken_anchor\"]; omit to run them all")] string[]? checks = null,
        [Description("Page size (server-capped)")] int? maxResults = null,
        [Description("Continuation cursor from a previous truncated response")] string? cursor = null,
        CancellationToken ct = default) =>
        support.Run("vault_lint", () => lint.Lint(new LintQuery
        {
            PathPrefix = pathPrefix,
            Checks = checks,
            MaxResults = maxResults,
            Cursor = cursor,
        }, ct));
}
