using Knapper.Core.Generation;
using Knapper.Core.Options;
using Knapper.Core.Vault;
using YamlDotNet.Serialization;

namespace Knapper.Core.Query;

/// <summary>
/// vault_search_frontmatter (brief §6): structured queries over YAML
/// frontmatter — field existence, equality, substring — as a supplement to
/// text search. Candidates are the vault's .md files in deterministic
/// sorted order (cursor pagination like every other list). Files whose
/// frontmatter fails to parse are REPORTED, not skipped silently: broken
/// YAML must never be able to hide a match from a "no match" answer.
/// </summary>
public sealed class FrontmatterSearchService(
    VaultPathResolver resolver,
    VaultFileLister lister,
    VaultReadService reader,
    VaultGenerationCounter generation,
    VaultOptions options)
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    public FrontmatterSearchResult Search(FrontmatterQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query.Field))
            throw new KnapperException(VaultErrorCode.InvalidArgument, "field is required");
        if (query.Op is FrontmatterOp.Equals or FrontmatterOp.Contains && string.IsNullOrEmpty(query.Value))
            throw new KnapperException(VaultErrorCode.InvalidArgument, $"op {query.Op} requires a value");

        var generationStart = generation.Current;
        var fingerprint = QueryCursor.Fingerprint(
            "frontmatter", query.Field, query.Op, query.Value, query.PathPrefix);
        string? cursorPath = query.Cursor is null
            ? null
            : QueryCursor.Decode(query.Cursor, fingerprint).Path;
        var pageSize = Math.Clamp(query.MaxResults ?? options.MaxResultsPerPage, 1, options.MaxResultsPerPage);
        var deadline = Environment.TickCount64 + options.QueryTimeoutMs;

        var candidates = lister.CollectFilesSorted(
            query.PathPrefix,
            rel => rel.EndsWith(".md", StringComparison.OrdinalIgnoreCase),
            ct);

        var items = new List<FrontmatterMatch>();
        var unparseable = new List<string>();
        var scanned = 0;
        var truncatedByBudget = false;
        string? lastProcessed = null;

        foreach (var (relative, absolute) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (cursorPath is not null && string.CompareOrdinal(relative, cursorPath) <= 0)
                continue;
            if (items.Count == pageSize || Environment.TickCount64 >= deadline)
            {
                truncatedByBudget = true;
                break;
            }
            scanned++;
            lastProcessed = relative;

            Dictionary<string, object?>? frontmatter;
            try
            {
                var bytes = reader.ReadBytesChecked(resolver.Resolve(relative));
                var (content, _) = VaultReadService.DecodeStrict(bytes, relative);
                var (shape, block) = ExtractFrontmatterBlock(content);
                if (shape == FrontmatterShape.None)
                    continue; // no frontmatter — scanned, honestly no match
                if (shape == FrontmatterShape.Malformed)
                {
                    // An unterminated fence could be hiding a match; treating
                    // it as "no frontmatter" would forge an exhaustive no.
                    unparseable.Add(relative);
                    continue;
                }
                frontmatter = Yaml.Deserialize<Dictionary<string, object?>>(block!);
            }
            catch (Exception e) when (e is KnapperException or YamlDotNet.Core.YamlException)
            {
                unparseable.Add(relative);
                continue;
            }
            if (frontmatter is null || !frontmatter.TryGetValue(query.Field, out var value))
                continue;

            var matches = query.Op switch
            {
                FrontmatterOp.Exists => true,
                FrontmatterOp.Equals => ValuesOf(value).Any(v =>
                    string.Equals(v, query.Value, StringComparison.OrdinalIgnoreCase)),
                FrontmatterOp.Contains => ValuesOf(value).Any(v =>
                    v.Contains(query.Value!, StringComparison.OrdinalIgnoreCase)),
                _ => false,
            };
            if (matches)
                items.Add(new FrontmatterMatch(relative, query.Field, Render(value)));
        }

        if (truncatedByBudget && items.Count == 0 && scanned == 0)
        {
            throw new KnapperException(VaultErrorCode.QueryTimeout,
                $"frontmatter search made no progress within {options.QueryTimeoutMs} ms");
        }

        var generationEnd = generation.Current;
        var envelope = new QueryEnvelope<FrontmatterMatch>(
            items,
            truncatedByBudget,
            truncatedByBudget && lastProcessed is not null
                ? QueryCursor.Encode(fingerprint, lastProcessed)
                : null,
            scanned,
            items.Count,
            truncatedByBudget ? null : items.Count + CountBehindCursor(cursorPath),
            generationStart,
            generationEnd,
            generationEnd != generationStart);
        return new FrontmatterSearchResult(envelope, unparseable);

        // TotalMatches across ALL pages is only known on a first, complete
        // page (no cursor, no budget hit); continuation pages report null
        // rather than a guess.
        long? CountBehindCursor(string? cursor) => cursor is null ? 0 : null;
    }

    internal enum FrontmatterShape
    {
        /// <summary>No opening fence on line 1 — honestly no frontmatter.</summary>
        None,
        /// <summary>A fenced block was found; Block carries its YAML.</summary>
        Present,
        /// <summary>
        /// An opening fence with no closing fence. NOT "no frontmatter": the
        /// file intended frontmatter and it could be hiding a match, so the
        /// caller must report it in UnparseableFiles — "no match" claims the
        /// scope was exhaustively searched.
        /// </summary>
        Malformed,
    }

    /// <summary>
    /// The YAML between a leading "---" line and the next "---"/"..." line.
    /// Empty-block files parse to an empty map.
    /// </summary>
    internal static (FrontmatterShape Shape, string? Block) ExtractFrontmatterBlock(string content)
    {
        var lines = VaultReadService.SplitLines(content);
        if (lines.Count == 0 || lines[0].TrimEnd('\r') != "---")
            return (FrontmatterShape.None, null);
        for (var i = 1; i < lines.Count; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line is "---" or "...")
                return (FrontmatterShape.Present, string.Join('\n', lines.Skip(1).Take(i - 1)));
        }
        return (FrontmatterShape.Malformed, null);
    }

    /// <summary>Scalar → itself; list → each element. Nested maps don't match value ops.</summary>
    private static IEnumerable<string> ValuesOf(object? value) => value switch
    {
        null => [],
        string s => [s],
        System.Collections.IEnumerable list => list.Cast<object?>()
            .Where(v => v is not null and not System.Collections.IDictionary)
            .Select(v => v!.ToString() ?? ""),
        _ => [value.ToString() ?? ""],
    };

    private static string? Render(object? value) => value switch
    {
        null => null,
        string s => s,
        System.Collections.IEnumerable list => string.Join(", ",
            list.Cast<object?>().Select(v => v?.ToString() ?? "")),
        _ => value.ToString(),
    };
}
