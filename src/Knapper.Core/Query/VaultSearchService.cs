using System.Text.Json;
using Knapper.Core.Generation;
using Knapper.Core.Options;
using Knapper.Core.Vault;

namespace Knapper.Core.Query;

/// <summary>
/// vault_search (brief §6): server-side ripgrep with structured args and
/// structured results. Three output shapes — match records, filenames-only,
/// per-file counts — all wearing the completeness envelope. Pagination is
/// deterministic (rg --sort=path + (path,line,column) cursors); budgets
/// surface as <c>truncated</c> + cursor, and a time budget that produced
/// nothing at all is a typed QueryTimeout, never an empty "no match".
/// </summary>
public sealed class VaultSearchService(
    VaultPathResolver resolver,
    VaultGenerationCounter generation,
    VaultOptions options)
{
    private readonly RipgrepRunner _runner = new(options.RipgrepPath);

    /// <summary>Test seam: runs after generation_start is captured, before rg starts.</summary>
    internal Action? OnQueryStarted;

    public QueryEnvelope<SearchMatch> SearchMatches(VaultSearchQuery query, CancellationToken ct = default)
    {
        var plan = Prepare(query, SearchMode.Matches);
        var args = new List<string> { "--json", "--line-number" };
        if (query.ContextBefore > 0)
            args.AddRange(["-B", query.ContextBefore.ToString()]);
        if (query.ContextAfter > 0)
            args.AddRange(["-A", query.ContextAfter.ToString()]);
        AddCommonArgs(args, query);

        var state = new MatchStream(query, plan) { OutputBudget = options.MaxOutputBytes };
        var outcome = RunWithHook(args, plan, ct, state.OnLine);
        // rg emits begin events only for files it reports on; the honest
        // files-searched count lives in the end-of-stream summary. When the
        // stream was cut short there is no summary — files-with-matches-so-far
        // is the best truthful lower bound.
        return Finish(plan, state.Items, state.LastPosition, state.TotalSeen,
            state.SummaryScanned ?? state.ScannedFiles, outcome);
    }

    public QueryEnvelope<string> SearchFilesOnly(VaultSearchQuery query, CancellationToken ct = default)
    {
        var plan = Prepare(query, SearchMode.FilesOnly);
        // Newline-separated paths (vault filenames cannot contain newlines —
        // Obsidian forbids them). No begin events → scanned_files is an
        // honest null, not a guess.
        var args = new List<string> { "-l" };
        AddCommonArgs(args, query);

        var items = new List<string>();
        (string, int, int)? last = null;
        var hasMore = false;
        var outcome = RunWithHook(args, plan, ct, line =>
        {
            if (line.Length == 0)
                return true;
            var pos = (line, 0, 0);
            if (plan.CursorPosition is { } cur && QueryCursor.ComparePosition(pos, cur) <= 0)
                return true;
            if (items.Count == plan.PageSize)
            {
                hasMore = true;
                return false;
            }
            items.Add(line);
            last = pos;
            return true;
        });
        return Finish(plan, items, last, totalSeen: null, scannedFiles: null, outcome, hasMore);
    }

    public QueryEnvelope<FileMatchCount> SearchCounts(VaultSearchQuery query, CancellationToken ct = default)
    {
        var plan = Prepare(query, SearchMode.Counts);
        // --null puts NUL between path and count, so paths containing ':'
        // can never corrupt the parse.
        var args = new List<string> { "--count-matches", "--null" };
        AddCommonArgs(args, query);

        var items = new List<FileMatchCount>();
        (string, int, int)? last = null;
        var hasMore = false;
        long sum = 0;
        var outcome = RunWithHook(args, plan, ct, line =>
        {
            var nul = line.IndexOf('\0');
            if (nul <= 0 || !long.TryParse(line[(nul + 1)..], out var count))
                return true;
            var path = line[..nul];
            sum += count;
            var pos = (path, 0, 0);
            if (plan.CursorPosition is { } cur && QueryCursor.ComparePosition(pos, cur) <= 0)
                return true;
            if (items.Count == plan.PageSize)
            {
                hasMore = true;
                return false;
            }
            items.Add(new FileMatchCount(path, count));
            last = pos;
            return true;
        });
        // TotalMatches = the full sum, known only when the whole scope was
        // counted from the start (no cursor, ran to completion).
        var total = outcome.Completed && plan.CursorPosition is null ? sum : (long?)null;
        return Finish(plan, items, last, total, scannedFiles: null, outcome, hasMore);
    }

    // ---- shared plumbing ----------------------------------------------

    private sealed record Plan(
        string Fingerprint,
        (string Path, int Line, int Column)? CursorPosition,
        int PageSize,
        IReadOnlyList<string> Prefixes,
        long GenerationStart);

    private Plan Prepare(VaultSearchQuery query, SearchMode mode)
    {
        if (string.IsNullOrEmpty(query.Pattern))
            throw new KnapperException(VaultErrorCode.InvalidArgument, "pattern is required");
        if (query.ContextBefore is < 0 or > 50 || query.ContextAfter is < 0 or > 50)
            throw new KnapperException(VaultErrorCode.InvalidArgument, "context must be between 0 and 50 lines");

        var prefixes = ValidatePrefixes(query.PathPrefixes);
        ValidateGlobs(query.IncludeGlobs);
        ValidateGlobs(query.ExcludeGlobs);
        _ = NormalizeExtensions(query.Extensions);

        var fingerprint = QueryCursor.Fingerprint(
            "search", mode, query.Pattern, query.Literal, query.Case, query.WholeWord,
            query.Multiline, prefixes, query.IncludeGlobs, query.ExcludeGlobs,
            query.Extensions, query.ContextBefore, query.ContextAfter);
        (string, int, int)? cursorPos = query.Cursor is null
            ? null
            : QueryCursor.Decode(query.Cursor, fingerprint);
        var pageSize = Math.Clamp(query.MaxResults ?? options.MaxResultsPerPage, 1, options.MaxResultsPerPage);
        return new Plan(fingerprint, cursorPos, pageSize, prefixes, generation.Current);
    }

    private void AddCommonArgs(List<string> args, VaultSearchQuery query)
    {
        args.Add(query.Case switch
        {
            CaseMode.Sensitive => "-s",
            CaseMode.Insensitive => "-i",
            _ => "-S",
        });
        if (query.Literal)
            args.Add("-F");
        if (query.WholeWord)
            args.Add("-w");
        if (query.Multiline)
            args.Add("-U");
        foreach (var glob in query.IncludeGlobs ?? [])
            args.Add("--glob=" + glob);
        foreach (var glob in query.ExcludeGlobs ?? [])
            args.Add("--glob=!" + glob);
        foreach (var ext in NormalizeExtensions(query.Extensions))
            args.Add("--glob=*." + ext);
        args.Add("-e");
        args.Add(query.Pattern);
        args.Add("--");
        foreach (var prefix in ValidatePrefixes(query.PathPrefixes))
            args.Add(prefix);
    }

    private RipgrepRunner.Outcome RunWithHook(
        List<string> args, Plan plan, CancellationToken ct, Func<string, bool> onLine)
    {
        OnQueryStarted?.Invoke();
        return _runner.Run(args, resolver.Root,
            TimeSpan.FromMilliseconds(options.QueryTimeoutMs), ct, onLine);
    }

    private QueryEnvelope<T> Finish<T>(
        Plan plan,
        List<T> items,
        (string Path, int Line, int Column)? lastPosition,
        long? totalSeen,
        int? scannedFiles,
        RipgrepRunner.Outcome outcome,
        bool hasMore = false)
    {
        if (outcome.TimedOut && items.Count == 0)
        {
            throw new KnapperException(VaultErrorCode.QueryTimeout,
                $"search produced no results within {options.QueryTimeoutMs} ms — narrow the scope " +
                "(path prefix, globs) or raise the budget");
        }
        var truncated = outcome.TimedOut || outcome.StoppedEarly || hasMore;
        var cursor = truncated && lastPosition is { } last
            ? QueryCursor.Encode(plan.Fingerprint, last.Path, last.Line, last.Column)
            : null;
        var genEnd = generation.Current;
        return new QueryEnvelope<T>(
            items,
            truncated,
            cursor,
            scannedFiles,
            items.Count,
            outcome.Completed ? totalSeen : null,
            plan.GenerationStart,
            genEnd,
            genEnd != plan.GenerationStart);
    }

    private IReadOnlyList<string> ValidatePrefixes(IReadOnlyList<string>? prefixes)
    {
        if (prefixes is null || prefixes.Count == 0)
            return [];
        var resolved = prefixes.Select(p => resolver.Resolve(p)).ToList();
        foreach (var vp in resolved.Where(vp => !Directory.Exists(vp.Absolute)))
        {
            throw new KnapperException(VaultErrorCode.NotFound,
                $"path prefix does not exist or is not a directory: {vp.Relative}");
        }
        var sorted = resolved.Select(vp => vp.Relative).Order(StringComparer.Ordinal).ToList();
        for (var i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] == sorted[i - 1] || sorted[i].StartsWith(sorted[i - 1] + '/', StringComparison.Ordinal))
            {
                throw new KnapperException(VaultErrorCode.InvalidArgument,
                    $"path prefixes overlap ('{sorted[i - 1]}' covers '{sorted[i]}') — overlapping scopes " +
                    "would duplicate results, which the completeness contract forbids");
            }
        }
        return sorted;
    }

    private static void ValidateGlobs(IReadOnlyList<string>? globs)
    {
        foreach (var glob in globs ?? [])
        {
            if (string.IsNullOrWhiteSpace(glob) || glob.Contains('\0'))
                throw new KnapperException(VaultErrorCode.InvalidArgument, "glob is empty or contains NUL");
        }
    }

    private static IReadOnlyList<string> NormalizeExtensions(IReadOnlyList<string>? extensions)
    {
        if (extensions is null || extensions.Count == 0)
            return [];
        return extensions.Select(e =>
        {
            var ext = e.TrimStart('.');
            if (ext.Length == 0 || !ext.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '+' or '~'))
            {
                throw new KnapperException(VaultErrorCode.InvalidArgument,
                    $"'{e}' is not a plain file extension — use include_globs for patterns");
            }
            return ext;
        }).ToList();
    }

    /// <summary>
    /// Streaming state for --json match mode. One record per SUBMATCH (a
    /// line containing the pattern twice yields two records), so
    /// total_matches aligns with rg --count-matches. Context arrays carry
    /// rg's context events only; matched lines are not echoed into a
    /// neighbor's context.
    /// </summary>
    private sealed class MatchStream(VaultSearchQuery query, Plan plan)
    {
        public readonly List<SearchMatch> Items = [];
        public (string Path, int Line, int Column)? LastPosition;
        public long TotalSeen;
        public int ScannedFiles;
        public int? SummaryScanned;

        // Cached UTF-8 sizes: the budget is MaxOutputBYTES, and .NET string
        // Length is UTF-16 code units — counting Length lets Unicode-heavy
        // pages blow past the configured budget.
        private readonly Queue<(string Text, int Bytes)> _before = new();
        private readonly List<(List<string> Sink, int Remaining)> _afterNeeds = [];
        private long _outputBytes;
        private bool _budgetHit;

        public bool OnLine(string line)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "begin":
                    ScannedFiles++;
                    _before.Clear();
                    _afterNeeds.Clear();
                    return true;

                case "context":
                {
                    var text = TrimNewline(GetLinesText(root));
                    var bytes = System.Text.Encoding.UTF8.GetByteCount(text);
                    for (var i = _afterNeeds.Count - 1; i >= 0; i--)
                    {
                        var (sink, remaining) = _afterNeeds[i];
                        sink.Add(text);
                        // Counted per DELIVERY: only lines that reach an
                        // emitted match's context are output; a line that
                        // feeds two matches is emitted twice.
                        _outputBytes += bytes;
                        if (remaining == 1)
                            _afterNeeds.RemoveAt(i);
                        else
                            _afterNeeds[i] = (sink, remaining - 1);
                    }
                    if (query.ContextBefore > 0)
                    {
                        // Speculative until a match copies it — counted then.
                        _before.Enqueue((text, bytes));
                        while (_before.Count > query.ContextBefore)
                            _before.Dequeue();
                    }
                    // Context participates in the budget DECISION, not just
                    // the count: after-context alone can cross the limit, and
                    // the next match must then close the page rather than be
                    // admitted first. At end-of-stream a crossed budget with
                    // no further match means everything was emitted — the
                    // result is complete, merely at its bounded overshoot.
                    NoteBudget();
                    return true;
                }

                case "match":
                {
                    var data = root.GetProperty("data");
                    if (!data.GetProperty("path").TryGetProperty("text", out var pathProp))
                        return true; // non-UTF-8 filename: cannot address it, skip
                    var path = pathProp.GetString()!;
                    var lineNumber = data.GetProperty("line_number").GetInt32();
                    var text = TrimNewline(GetLinesText(root));
                    var textBytes = System.Text.Encoding.UTF8.GetByteCount(text);

                    foreach (var sub in data.GetProperty("submatches").EnumerateArray())
                    {
                        var column = sub.GetProperty("start").GetInt32() + 1;
                        TotalSeen++;
                        var pos = (path, lineNumber, column);
                        // Pre-cursor records were emitted on an EARLIER page;
                        // skipping them must not consume this page's budget.
                        if (plan.CursorPosition is { } cur && QueryCursor.ComparePosition(pos, cur) <= 0)
                            continue;
                        if (Items.Count == plan.PageSize || _budgetHit)
                            return false; // page full — the record we just saw proves more exist
                        List<string>? after = null;
                        if (query.ContextAfter > 0)
                        {
                            after = [];
                            _afterNeeds.Add((after, query.ContextAfter));
                        }
                        string[]? before = null;
                        if (query.ContextBefore > 0)
                        {
                            before = [.. _before.Select(b => b.Text)];
                            _outputBytes += _before.Sum(b => b.Bytes); // this match's own copy
                        }
                        Items.Add(new SearchMatch(path, lineNumber, column, text, before, after));
                        _outputBytes += textBytes; // per emitted record — each carries the line
                        LastPosition = pos;
                        // Per emitted RECORD, so a second submatch on this
                        // same line is not admitted past the budget. The
                        // one-record-over rule: the page closes at the next
                        // record after the running total (match lines + all
                        // delivered context, UTF-8 bytes) exceeds the budget,
                        // bounding overshoot to one match plus its context.
                        NoteBudget();
                    }
                    return true;
                }

                case "summary":
                    if (root.GetProperty("data").TryGetProperty("stats", out var stats)
                        && stats.TryGetProperty("searches", out var searches))
                    {
                        SummaryScanned = searches.GetInt32();
                    }
                    return true;

                default:
                    return true; // end
            }
        }

        public int OutputBudget { get; init; } = int.MaxValue;

        /// <summary>A page always makes progress: the budget never closes a page that has no items yet.</summary>
        private void NoteBudget()
        {
            if (_outputBytes > OutputBudget && Items.Count > 0)
                _budgetHit = true;
        }

        private static string GetLinesText(JsonElement root) =>
            root.GetProperty("data").GetProperty("lines").TryGetProperty("text", out var t)
                ? t.GetString() ?? ""
                : ""; // non-UTF-8 line payload ("bytes") — represented as empty, match position still reported

        private static string TrimNewline(string s) => s.TrimEnd('\n', '\r');
    }
}
