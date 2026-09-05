using Knapper.Core.Generation;
using Knapper.Core.Options;
using Knapper.Core.Vault;
using YamlDotNet.Serialization;

namespace Knapper.Core.Query;

/// <summary>
/// Read-only consistency checks over the vault's link graph
/// (docs/proposals/vault-lint.md). Slice one: the four checks that share one
/// parser, which is every finding the 2026-08-30 measurement of Helios turned
/// up — 27 unresolved links, 18 stale heading fragments, 22 table rows broken
/// by an unescaped pipe, 1 ambiguous link. The per-file structural checks the
/// proposal also tiers as "structural" (fence balance, frontmatter, empty
/// file) found nothing on that vault and are deliberately absent.
///
/// <c>table_needs_blank_line</c> (added 2026-09-01) is the first structural
/// check, and it earned its place the same way: measured, not reasoned about.
/// A table whose header row has no blank line above it is absorbed into the
/// paragraph above and renders as literal pipes — the whole Setbacks table of
/// 'Home/Mayapple/Projects/Screened Porch Project.md', which is what put this
/// check here. A whole-vault sweep on that date found 12 tables preceded by a
/// non-blank line, and all 12 were then looked at in Obsidian: 9 sit under a
/// heading and render correctly, and 3 do not render at all — that one, plus
/// two nested inside bullets in 'Tech/Homelab/Homelab Roadmap.md', which is
/// what widened the rule from "a paragraph absorbs a table" to "so does a
/// bullet, at any indent". It stays in the link
/// service, and in the link parser, because it is a verdict about the SAME
/// table scan table_pipe reads — split across two parsers, one would report a
/// phantom column inside a block the other knows is not a table.
///
/// Deliberately NOT here, and both are the proposal's own decisions:
/// findings are OBSERVATIONS (§7) — nothing in this service can write, and a
/// cluster of related findings usually means a decision about intent rather
/// than an edit; and there is no git BASELINE yet (§5), so a whole-vault run
/// reports the standing backlog rather than "what changed under you". Until
/// the baseline lands this belongs on an agent-scoped path prefix, not on a
/// timer.
/// </summary>
public sealed class VaultLintService(
    VaultPathResolver resolver,
    VaultFileLister lister,
    VaultReadService reader,
    VaultGenerationCounter generation,
    VaultOptions options,
    ArchivedPrefixes archived)
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    public LintResult Lint(LintQuery query, CancellationToken ct = default)
    {
        var checks = ResolveChecks(query.Checks);
        var generationStart = generation.Current;
        var fingerprint = QueryCursor.Fingerprint(
            "lint", query.PathPrefix, string.Join(',', checks.Order(StringComparer.Ordinal)));
        var cursor = query.Cursor is null
            ? null
            : ((string, int, int, string)?)QueryCursor.DecodeKeyed(query.Cursor, fingerprint);
        var pageSize = Math.Clamp(query.MaxResults ?? options.MaxResultsPerPage, 1, options.MaxResultsPerPage);
        var deadline = Environment.TickCount64 + options.QueryTimeoutMs;

        // The scoped walk runs first because it is what validates PathPrefix
        // (VaultFileLister owns that check; a second spelling of it here is
        // how two path gates drift apart).
        // Archived notes drop out of REPORTING only. The index below stays
        // whole-vault, exactly as it does for PathPrefix and for the same
        // reason: a link inside the scope can point anywhere, and an archived
        // note is a perfectly valid link TARGET. Excluding them from the index
        // too would turn every link into the archive into a false
        // unresolved_link — the check reporting damage that is not there.
        var excluded = archived.ExcludedFor(query.PathPrefix);
        var scoped = lister.CollectFilesSorted(
            query.PathPrefix, rel => IsMarkdown(rel) && !excluded.Covers(rel), ct);
        var index = BuildIndex(ct, deadline);

        var findings = new List<LintFinding>();
        var perFile = new List<LintFinding>();
        foreach (var (relative, _) in scoped)
        {
            ct.ThrowIfCancellationRequested();
            if (!index.Shapes.TryGetValue(relative, out var shape))
                continue; // unreadable: already reported in UnexaminedFiles
            perFile.Clear();
            foreach (var link in shape.Links)
                Check(index, relative, link, checks, perFile);
            if (checks.Contains(LintChecks.TableNeedsBlankLine))
            {
                foreach (var table in shape.AbsorbedTables)
                {
                    perFile.Add(new LintFinding(
                        LintChecks.TableNeedsBlankLine, relative, table.Header, table.Line, table.Column,
                        "no blank line above this table, so Obsidian absorbs it into the paragraph above and " +
                        "renders every row as literal text; insert a blank line before the header row"));
                }
            }
            // Links come out in document order and tables in their own, so
            // the two have to be merged rather than appended: the page cursor
            // is a POSITION, and a finding sitting behind an earlier one it
            // was emitted after would be skipped on the next page.
            //
            // The check name is part of the sort for a sharper reason: one
            // wikilink can be BOTH an unescaped pipe in a table row and an
            // unresolved target, so a position is not unique here and the
            // cursor carries the name to break the tie. Sort and cursor read
            // the same key, in the same order, or the tie-break omits instead
            // of fixing.
            findings.AddRange(perFile
                .OrderBy(f => f.Line)
                .ThenBy(f => f.Column)
                .ThenBy(f => f.Check, StringComparer.Ordinal));
        }

        // Truncation is decided against what REMAINS after the cursor, not
        // against the total: a final page that exactly fills the page size
        // would otherwise claim truncated=true and hand out a cursor for an
        // empty page — an over-claim in the one field the completeness
        // contract makes a strong promise about.
        var remaining = cursor is null
            ? findings
            : [.. findings.Where(f =>
                QueryCursor.ComparePosition((f.Path, f.Line, f.Column, f.Check), cursor.Value) > 0)];
        var truncated = remaining.Count > pageSize;
        var page = truncated ? remaining[..pageSize] : remaining;
        var generationEnd = generation.Current;
        return new LintResult(
            page,
            truncated,
            truncated
                ? QueryCursor.Encode(fingerprint, page[^1].Path, page[^1].Line, page[^1].Column, page[^1].Check)
                : null,
            index.ScannedFiles,
            page.Count,
            // The whole graph is built before any finding is emitted, so the
            // total is known even mid-pagination (as in VaultFileLister).
            findings.Count,
            generationStart,
            generationEnd,
            generationEnd != generationStart,
            index.Unexamined,
            excluded.Prefixes);
    }

    private static void Check(
        LinkIndex index, string from, WikiLink.Ref link, HashSet<string> checks, List<LintFinding> findings)
    {
        // A table cell breaks whether or not the link resolves, and whether
        // or not it is an embed — the pipe is read by the TABLE parser before
        // anything looks at the link.
        if (link.InTableRow && link.HasUnescapedPipe && checks.Contains(LintChecks.TablePipe))
        {
            findings.Add(new LintFinding(
                LintChecks.TablePipe, from, link.Raw, link.Line, link.Column,
                "an unescaped '|' inside this wikilink opens a phantom table column; write it as '\\|'"));
        }

        // Embeds are excluded from resolution, and not only for §8's reason
        // that an attachment is not a missing note: §11's size ceiling is
        // symmetric, so an attachment over the Sync limit never reaches this
        // replica at all. Reporting those as unresolved would turn a known
        // blind spot into a stream of confident false findings.
        if (link.IsEmbed)
            return;

        var subject = link.Fragment is null
            ? link.Target
            : $"{link.Target}#{(link.FragmentIsBlockId ? "^" : "")}{link.Fragment}";

        var targets = link.Target.Length == 0 ? [from] : index.Resolve(link.Target, from);
        if (targets.Count == 0)
        {
            if (checks.Contains(LintChecks.UnresolvedLink))
            {
                findings.Add(new LintFinding(
                    LintChecks.UnresolvedLink, from, subject, link.Line, link.Column,
                    $"no vault file matches '{link.Target}'"));
            }
            return;
        }
        if (targets.Count > 1)
        {
            if (checks.Contains(LintChecks.AmbiguousLink))
            {
                findings.Add(new LintFinding(
                    LintChecks.AmbiguousLink, from, subject, link.Line, link.Column,
                    $"'{link.Target}' matches {targets.Count} files ({string.Join(", ", targets)}) — " +
                    "Obsidian silently picks one; use a full path"));
            }
            return; // which file the anchor belongs to is exactly what is undecided
        }

        if (link.Fragment is null || !checks.Contains(LintChecks.BrokenAnchor))
            return;
        var target = targets[0];
        // The file exists but its text could not be read, so its headings are
        // UNKNOWN. Reporting a broken anchor here would be a guess, and the
        // file is named in UnexaminedFiles so the silence is accounted for.
        if (!index.Shapes.TryGetValue(target, out var targetShape))
            return;

        var found = link.FragmentIsBlockId
            ? targetShape.BlockIds.Contains(link.Fragment, StringComparer.Ordinal)
            : targetShape.Headings.Contains(WikiLink.NormalizeHeading(link.Fragment), StringComparer.Ordinal);
        if (!found)
        {
            findings.Add(new LintFinding(
                LintChecks.BrokenAnchor, from, subject, link.Line, link.Column,
                link.FragmentIsBlockId
                    ? $"'{target}' has no block id '^{link.Fragment}'"
                    : $"'{target}' has no heading '{link.Fragment}'"));
        }
    }

    private LinkIndex BuildIndex(CancellationToken ct, long deadline)
    {
        var all = lister.CollectFilesSorted(null, static _ => true, ct);
        var index = new LinkIndex();
        foreach (var (relative, _) in all)
            index.AddFile(relative);

        var scanned = 0;
        foreach (var (relative, _) in all)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsMarkdown(relative))
                continue;
            if (Environment.TickCount64 >= deadline)
            {
                // NOT a truncated page. A half-built index does not report
                // fewer findings, it reports WRONG ones: every link into a
                // note that was not reached yet looks unresolved. The index
                // is complete or the query fails.
                throw new KnapperException(VaultErrorCode.QueryTimeout,
                    $"building the link index exceeded {options.QueryTimeoutMs} ms; a partial index would " +
                    "report links into unindexed notes as unresolved, so no findings are returned");
            }
            scanned++;
            string content;
            try
            {
                var bytes = reader.ReadBytesChecked(resolver.Resolve(relative));
                (content, _) = VaultReadService.DecodeStrict(bytes, relative);
            }
            catch (KnapperException)
            {
                // Still a valid link TARGET (the file exists); only its
                // headings and its own links are unknown.
                index.Unexamined.Add(relative);
                continue;
            }
            index.Shapes[relative] = WikiLink.Parse(content);
            foreach (var alias in AliasesOf(content))
                index.AddAlias(alias, relative);
        }

        index.ScannedFiles = scanned;
        return index;
    }

    /// <summary>Frontmatter <c>aliases</c> (or <c>alias</c>), scalar or list. Broken YAML simply has none.</summary>
    private static IEnumerable<string> AliasesOf(string content)
    {
        var (shape, block) = FrontmatterSearchService.ExtractFrontmatterBlock(content);
        if (shape != FrontmatterSearchService.FrontmatterShape.Present || block is null)
            return [];
        Dictionary<string, object?>? map;
        try
        {
            map = Yaml.Deserialize<Dictionary<string, object?>>(block);
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return [];
        }
        if (map is null)
            return [];
        object? value = null;
        foreach (var key in (string[])["aliases", "alias"])
        {
            if (map.TryGetValue(key, out value) && value is not null)
                break;
        }
        return value switch
        {
            null => [],
            string s => [s],
            System.Collections.IEnumerable list => list.Cast<object?>()
                .Where(v => v is not null and not System.Collections.IDictionary)
                .Select(v => v!.ToString() ?? "")
                .Where(s => s.Length > 0),
            _ => [value.ToString() ?? ""],
        };
    }

    private static HashSet<string> ResolveChecks(IReadOnlyList<string>? requested)
    {
        if (requested is null || requested.Count == 0)
            return [.. LintChecks.All];
        var unknown = requested.Where(c => !LintChecks.All.Contains(c, StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0)
        {
            throw new KnapperException(VaultErrorCode.InvalidArgument,
                $"unknown check(s): {string.Join(", ", unknown)}. Known: {string.Join(", ", LintChecks.All)}");
        }
        return [.. requested];
    }

    private static bool IsMarkdown(string relative) =>
        relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Filename, path and alias lookup for link targets.
    ///
    /// Matching is CASE-INSENSITIVE because that is Obsidian's link
    /// semantics. This is not the case-folding CLAUDE.md forbids: that rule
    /// governs lock identity, batch de-duplication and path comparison, where
    /// folding aliases two real files into one. Here nothing is folded away —
    /// two notes differing only by case both stay in the list and surface as
    /// <c>ambiguous_link</c>, which is the honest answer on a case-sensitive
    /// vault.
    /// </summary>
    private sealed class LinkIndex
    {
        private readonly Dictionary<string, List<string>> _byPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _byName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _byAlias = new(StringComparer.OrdinalIgnoreCase);

        internal Dictionary<string, WikiLink.NoteShape> Shapes { get; } = new(StringComparer.Ordinal);
        internal List<string> Unexamined { get; } = [];
        internal int ScannedFiles { get; set; }

        internal void AddFile(string relative)
        {
            Add(_byPath, relative, relative);
            Add(_byPath, StripMarkdown(relative), relative);
            var slash = relative.LastIndexOf('/');
            var name = slash < 0 ? relative : relative[(slash + 1)..];
            Add(_byName, name, relative);
            Add(_byName, StripMarkdown(name), relative);
        }

        internal void AddAlias(string alias, string relative) => Add(_byAlias, alias.Trim(), relative);

        internal IReadOnlyList<string> Resolve(string target, string from)
        {
            var key = target.Trim();
            if (key.StartsWith("./", StringComparison.Ordinal))
                key = key[2..];
            if (key.Length == 0)
                return [];
            var folder = FolderOf(from);

            // A target containing '/' is a path: from the vault ROOT first,
            // then RELATIVE to the linking note's folder. The relative form
            // is not a nicety — Helios links to
            // 'Proxmox/Homelab Monthly Maintenance' from Tech/Homelab/, and
            // root-only matching calls that broken while Obsidian follows it.
            // It is still not matched as an arbitrary path SUFFIX: that would
            // resolve a path naming the wrong parent, which is the defect the
            // 2026-08-30 pass reported for the InfluxDB notes.
            if (key.Contains('/', StringComparison.Ordinal))
            {
                if (_byPath.TryGetValue(key, out var byPath))
                    return Narrow(byPath, key, folder);
                var relative = folder.Length == 0 ? key : folder + '/' + key;
                return _byPath.TryGetValue(relative, out var byRelative)
                    ? Narrow(byRelative, relative, folder)
                    : [];
            }

            if (_byName.TryGetValue(key, out var byName))
                return Narrow(byName, key, folder);
            return _byAlias.TryGetValue(key, out var byAlias) ? Narrow(byAlias, key, folder) : [];
        }

        /// <summary>
        /// Obsidian's tie-breaks, in its order, applied before a link is
        /// called ambiguous. Both were measured as false positives on Helios:
        /// [[CLAUDE]] matches CLAUDE.md and Tech/Claude.md only because
        /// lookup is case-insensitive, and an exact-case match settles it;
        /// [[Cabinets]] matches two notes, but the linking note sits in the
        /// folder of one of them and Obsidian resolves to the nearest.
        /// Reporting either is noise about a link that behaves exactly as
        /// written — and ambiguous_link is only worth having while it means
        /// "Obsidian's choice here is arbitrary".
        /// </summary>
        private static IReadOnlyList<string> Narrow(
            IReadOnlyList<string> candidates, string key, string folder)
        {
            if (candidates.Count < 2)
                return candidates;
            var exact = candidates
                .Where(c => string.Equals(StripMarkdown(c), key, StringComparison.Ordinal)
                    || string.Equals(StripMarkdown(NameOf(c)), key, StringComparison.Ordinal))
                .ToList();
            if (exact.Count == 1)
                return exact;
            var pool = exact.Count > 1 ? exact : candidates;
            var nearest = pool.Where(c => FolderOf(c) == folder).ToList();
            return nearest.Count == 1 ? nearest : pool;
        }

        private static string FolderOf(string relative)
        {
            var slash = relative.LastIndexOf('/');
            return slash < 0 ? "" : relative[..slash];
        }

        private static string NameOf(string relative)
        {
            var slash = relative.LastIndexOf('/');
            return slash < 0 ? relative : relative[(slash + 1)..];
        }

        private static string StripMarkdown(string path) =>
            path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? path[..^3] : path;

        private static void Add(Dictionary<string, List<string>> map, string key, string relative)
        {
            if (key.Length == 0)
                return;
            if (!map.TryGetValue(key, out var list))
                map[key] = list = [];
            if (!list.Contains(relative, StringComparer.Ordinal))
                list.Add(relative);
        }
    }
}
