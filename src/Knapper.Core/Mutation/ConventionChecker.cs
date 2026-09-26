using System.Text;
using System.Text.RegularExpressions;
using Knapper.Core.Options;
using Knapper.Core.Query;

namespace Knapper.Core.Mutation;

/// <summary>
/// The write-side convention checks (<c>Conventions:*</c>): what a committed
/// write broke, reported as warnings on its receipt.
///
/// <para>Every check is about what the write ADDED, judged against the bytes
/// the mutation already held from its fresh read. A note that already had a
/// markdown link, frontmatter or tags before an agent touched it is not the
/// agent's doing, and a warning repeated on every edit of such a note is a
/// warning agents learn to skip.</para>
///
/// <para>ADVISORY, and it must stay that way. It runs after the write has
/// committed and verified, and it never throws: a bug here that surfaced as
/// an exception would turn a landed, verified write into an error receipt
/// — the one outcome worse than a missed warning, because the agent would
/// retry a write that already happened. Anything it cannot judge (not
/// Markdown, not UTF-8, unparseable YAML) yields no warning.</para>
///
/// <para>Pure over bytes, like <see cref="WikiLink"/>, and shares its fence
/// and inline-code rules so the lint and this agree about what is code.</para>
/// </summary>
public sealed class ConventionChecker(ConventionsOptions options, KnapperMetrics? metrics = null)
{
    public static readonly ConventionChecker Off = new(new ConventionsOptions());

    internal const string MarkdownInternalLink = "markdown_internal_link";
    internal const string FrontmatterAdded = "frontmatter_added";
    internal const string TagsAdded = "tags_added";
    internal const string NoteAtVaultRoot = "note_at_vault_root";

    private const int MaxExamples = 3;
    private static readonly IReadOnlyList<ConventionWarning> None = [];

    // [text](target), ![alt](target), with an optional <bracketed> target and
    // an optional "title". The target is captured WITHOUT the brackets so
    // <Notes/a b.md> and Notes/a%20b.md are judged the same way.
    private static readonly Regex MarkdownLink = new(
        @"(?<!\\)!?\[[^\]\r\n]*\]\(\s*(?:<(?<t>[^>\r\n]*)>|(?<t>[^)\s]*))(?:\s+(?:""[^""\r\n]*""|'[^'\r\n]*'))?\s*\)",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    // A URI scheme (https:, mailto:, obsidian:) or a protocol-relative //host.
    private static readonly Regex External = new(
        @"^(?:[A-Za-z][A-Za-z0-9+.\-]*:|//)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    // An Obsidian inline tag: '#' at line start or after whitespace, then tag
    // characters, at least one of them non-numeric (#2026 is not a tag).
    private static readonly Regex InlineTag = new(
        @"(?<!\S)#(?<tag>[\p{L}\p{N}_/\-]*[\p{L}_/\-][\p{L}\p{N}_/\-]*)",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    /// <summary>
    /// Warnings for one committed write. <paramref name="before"/> is null for
    /// a created file: frontmatter and tag checks then do not apply, and
    /// placement does.
    /// </summary>
    public IReadOnlyList<ConventionWarning> Check(string relativePath, byte[]? before, byte[] after) =>
        Counted(Compute(relativePath, before, after));

    /// <summary>
    /// Warnings for a MOVE's destination. A move changes no bytes, so only
    /// placement can be judged.
    /// </summary>
    public IReadOnlyList<ConventionWarning> CheckPlacement(string relativePath)
    {
        if (!IsNote(relativePath) || !options.ChecksPlacement)
            return None;
        var warnings = new List<ConventionWarning>();
        AddPlacement(relativePath, warnings);
        return Counted(warnings);
    }

    private IReadOnlyList<ConventionWarning> Compute(string relativePath, byte[]? before, byte[] after)
    {
        if (!IsNote(relativePath) || !(options.AnyChecked || options.ChecksPlacement))
            return None;
        var warnings = new List<ConventionWarning>();
        try
        {
            if (before is null && options.ChecksPlacement)
                AddPlacement(relativePath, warnings);
            if (!options.AnyChecked)
                return warnings;

            var afterText = Decode(after);
            if (afterText is null)
                return warnings;
            var beforeText = before is null ? null : Decode(before);
            if (before is not null && beforeText is null)
                return warnings; // the old bytes were not text: nothing to compare against

            if (options.WikilinksOnly)
                CheckLinks(beforeText ?? "", afterText, warnings);
            if (beforeText is not null && options.NoNewFrontmatter)
                CheckFrontmatter(beforeText, afterText, warnings);
            if (beforeText is not null && options.NoNewTags)
                CheckTags(beforeText, afterText, warnings);
            return warnings;
        }
        catch (Exception)
        {
            // Deliberately total — see the class remarks. The write has landed.
            return warnings;
        }
    }

    private IReadOnlyList<ConventionWarning> Counted(IReadOnlyList<ConventionWarning> warnings)
    {
        metrics?.RecordConventionWarnings(warnings.Count);
        return warnings;
    }

    private static bool IsNote(string relativePath) =>
        relativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

    private void AddPlacement(string relativePath, List<ConventionWarning> warnings)
    {
        if (relativePath.Contains('/'))
            return;
        var folder = options.NewNoteFolder!.Trim().Trim('/');
        warnings.Add(new ConventionWarning(NoteAtVaultRoot,
            $"this note is at the vault root, which is no folder at all; new notes go in {folder}/ here unless " +
            "a more specific folder fits — move it with vault_move."));
    }

    private static void CheckLinks(string before, string after, List<ConventionWarning> warnings)
    {
        var existing = InternalLinks(before).GroupBy(l => l, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var added = new List<string>();
        foreach (var link in InternalLinks(after))
        {
            if (existing.TryGetValue(link, out var n) && n > 0)
                existing[link] = n - 1;
            else
                added.Add(link);
        }
        if (added.Count == 0)
            return;
        warnings.Add(new ConventionWarning(MarkdownInternalLink,
            $"this write added {added.Count} markdown link(s) to vault content ({Examples(added)}); internal links " +
            "are [[wikilinks]] here (embeds: ![[…]]) — Obsidian does not track markdown links, so renames break them."));
    }

    private static void CheckFrontmatter(string before, string after, List<ConventionWarning> warnings)
    {
        if (FrontmatterSearchService.ExtractFrontmatterBlock(before).Shape != FrontmatterSearchService.FrontmatterShape.None)
            return;
        if (FrontmatterSearchService.ExtractFrontmatterBlock(after).Shape == FrontmatterSearchService.FrontmatterShape.None)
            return;
        warnings.Add(new ConventionWarning(FrontmatterAdded,
            "this write added a frontmatter block to a note that had none; this vault does not add frontmatter " +
            "to existing notes — move the content into the body."));
    }

    private static void CheckTags(string before, string after, List<ConventionWarning> warnings)
    {
        if (Tags(before) is not { Count: 0 })
            return; // already tagged, or could not tell: no warning either way
        if (Tags(after) is not { Count: > 0 } added)
            return;
        warnings.Add(new ConventionWarning(TagsAdded,
            $"this write added tags ({Examples(added)}) to a note that used none; this vault does not add tags " +
            "to notes that do not already use them."));
    }

    /// <summary>Markdown link targets outside fenced and inline code that are not URLs.</summary>
    private static IEnumerable<string> InternalLinks(string text)
    {
        foreach (var line in ProseLines(text))
        {
            foreach (Match m in MarkdownLink.Matches(line))
            {
                var target = m.Groups["t"].Value.Trim();
                if (target.Length > 0 && !External.IsMatch(target))
                    yield return m.Value;
            }
        }
    }

    /// <summary>
    /// Every tag the note carries — inline and frontmatter. Null when the
    /// frontmatter could not be parsed: "could not tell" must never read as
    /// "had none", or a note with broken YAML would warn on every edit.
    /// </summary>
    private static List<string>? Tags(string text)
    {
        var tags = new List<string>();
        var (shape, block) = FrontmatterSearchService.ExtractFrontmatterBlock(text);
        if (shape == FrontmatterSearchService.FrontmatterShape.Malformed)
            return null;
        if (shape == FrontmatterSearchService.FrontmatterShape.Present && block is not null)
        {
            Dictionary<string, object?>? map;
            try
            {
                map = FrontmatterYaml.Deserialize(block);
            }
            catch (YamlDotNet.Core.YamlException)
            {
                return null;
            }
            foreach (var key in new[] { "tags", "tag" })
            {
                if (map?.TryGetValue(key, out var value) == true)
                    tags.AddRange(ValuesOf(value).Where(v => v.Length > 0).Select(v => "#" + v.TrimStart('#')));
            }
        }
        foreach (var line in ProseLines(text, skipFrontmatter: true))
            tags.AddRange(InlineTag.Matches(line).Select(m => "#" + m.Groups["tag"].Value));
        return tags;
    }

    private static IEnumerable<string> ValuesOf(object? value) => value switch
    {
        null => [],
        string s => s.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries),
        System.Collections.IEnumerable list => list.Cast<object?>()
            .Where(v => v is not null and not System.Collections.IDictionary)
            .Select(v => v!.ToString() ?? ""),
        _ => [value.ToString() ?? ""],
    };

    /// <summary>Lines outside fenced code, with inline code masked.</summary>
    private static IEnumerable<string> ProseLines(string text, bool skipFrontmatter = false)
    {
        var lines = VaultReadService.SplitLines(text);
        var fenced = WikiLink.FencedLines(lines);
        var start = 0;
        if (skipFrontmatter && lines.Count > 0 && lines[0].TrimEnd('\r') == "---")
        {
            for (var i = 1; i < lines.Count; i++)
            {
                if (lines[i].TrimEnd('\r') is "---" or "...")
                {
                    start = i + 1;
                    break;
                }
            }
        }
        for (var i = start; i < lines.Count; i++)
        {
            if (!fenced[i])
                yield return WikiLink.MaskInlineCode(lines[i].TrimEnd('\r'));
        }
    }

    private static string Examples(IReadOnlyList<string> items)
    {
        var distinct = items.Distinct(StringComparer.Ordinal).ToList();
        var shown = string.Join(", ", distinct.Take(MaxExamples).Select(Clip));
        return distinct.Count > MaxExamples ? $"{shown}, …" : shown;
    }

    private static string Clip(string s) => s.Length <= 80 ? s : s[..77] + "…";

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    private static string? Decode(byte[] bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
