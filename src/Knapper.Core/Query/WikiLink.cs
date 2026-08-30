using System.Text;

namespace Knapper.Core.Query;

/// <summary>
/// The ONE wikilink parser, and the ONE place a note's link-relevant shape
/// (links, headings, block ids, table rows) is derived from its text.
///
/// It exists as a single pass because the four lint checks are not four
/// independent problems: the 22 "malformed table" findings measured on
/// Helios on 2026-08-30 are an unescaped <c>|</c> INSIDE a wikilink inside a
/// table row — a link defect wearing a table costume. A table checker written
/// beside a link checker sees the symptom and misdiagnoses the cause, and two
/// parsers would have to agree about fenced code, inline code and escaping to
/// stay consistent. One parser, four readings of its output.
///
/// What it deliberately does NOT do: resolve anything. Resolution needs the
/// whole-vault index and lives in <see cref="VaultLintService"/>; this class
/// is pure over one file's text so the false-positive corpus can be pinned
/// without a filesystem.
/// </summary>
internal static class WikiLink
{
    /// <summary>
    /// One <c>[[…]]</c> occurrence exactly as written. <c>Column</c> is a
    /// 1-based BYTE offset like <see cref="SearchMatch.Column"/> — the query
    /// surface's one convention; a char offset here would disagree with
    /// every other position this server reports.
    /// </summary>
    internal sealed record Ref(
        int Line,
        int Column,
        string Raw,
        bool IsEmbed,
        /// <summary>Text before '#'/'|'. Empty for a same-file link ([[#Heading]]).</summary>
        string Target,
        /// <summary>Text after the first '#', block marker stripped. Null when the link has no fragment.</summary>
        string? Fragment,
        bool FragmentIsBlockId,
        string? Alias,
        /// <summary>
        /// A '|' inside the link that a GFM table parser will read as a cell
        /// boundary. Only meaningful together with <see cref="InTableRow"/>;
        /// an escaped '\|' does not set it.
        /// </summary>
        bool HasUnescapedPipe,
        bool InTableRow);

    /// <summary>
    /// A note's parsed shape. Headings and block ids are what INBOUND links
    /// are checked against, so they are extracted under exactly the same
    /// fence rules as links: a '# comment' inside a shell fence is not a
    /// heading, and these notes are mostly shell.
    /// </summary>
    internal sealed record NoteShape(
        IReadOnlyList<Ref> Links,
        /// <summary>Normalized (see <see cref="NormalizeHeading"/>) heading texts, in document order.</summary>
        IReadOnlyList<string> Headings,
        IReadOnlyList<string> BlockIds);

    internal static NoteShape Parse(string content)
    {
        var lines = VaultReadService.SplitLines(content);
        var links = new List<Ref>();
        var headings = new List<string>();
        var blockIds = new List<string>();

        // Fence state, CommonMark-ish: a fence opens with 3+ backticks or
        // tildes and closes only on the SAME character with a run at least as
        // long. A ``` inside a ~~~ block closes nothing — which is why the
        // opening char and length are both carried rather than a bool.
        var fenceChar = '\0';
        var fenceLen = 0;
        // Precomputed in ONE forward pass. Deciding per line by walking back
        // to the block's first row is O(n²) over a long table, on a request
        // path that carries a wall-clock budget — and these notes run to
        // 200KB.
        var tableRows = MarkTableRows(lines);
        // The candidate for a setext underline on the NEXT line.
        string? previousParagraph = null;
        // The leading '---' block is YAML, not content: a '# comment' in a
        // frontmatter value is not a heading. Links inside it ARE real
        // (Obsidian resolves links in properties), so only heading/block-id
        // extraction skips it.
        var inFrontmatter = lines.Count > 0 && Trim(lines[0]) == "---";

        for (var i = 0; i < lines.Count; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            var lineNo = i + 1;

            if (inFrontmatter && i > 0 && Trim(raw) is "---" or "...")
            {
                inFrontmatter = false;
                continue;
            }

            if (TryFence(raw, out var ch, out var len))
            {
                if (fenceChar == '\0')
                {
                    (fenceChar, fenceLen) = (ch, len);
                    previousParagraph = null;
                    continue;
                }
                if (ch == fenceChar && len >= fenceLen && RestIsBlank(raw, ch))
                {
                    (fenceChar, fenceLen) = ('\0', 0);
                    continue;
                }
            }
            if (fenceChar != '\0')
            {
                previousParagraph = null;
                continue; // inside a fence: no links, no headings, no block ids
            }

            if (!inFrontmatter)
            {
                // Setext: a rule of '=' or '-' directly under a paragraph
                // line makes that line a heading, and Obsidian anchors it
                // like any other (measured 2026-08-30 — it appears in the
                // heading suggester as H1). Skipping them reports every link
                // to one as a broken anchor.
                if (previousParagraph is not null && IsSetextUnderline(raw))
                {
                    headings.Add(NormalizeHeading(previousParagraph));
                    previousParagraph = null;
                    continue;
                }
                if (TryHeading(raw, out var heading))
                {
                    headings.Add(heading);
                    previousParagraph = null;
                }
                else
                {
                    if (TryBlockId(raw, out var blockId))
                        blockIds.Add(blockId);
                    previousParagraph = raw.Trim().Length == 0 ? null : raw;
                }
            }

            // Inline code is masked, not removed, so byte columns stay true
            // to the file: `[[ -t 1 ]]` written inline is not a link.
            var scannable = MaskInlineCode(raw);
            var inTable = tableRows[i];
            foreach (var link in ScanLinks(scannable, raw, lineNo, inTable))
                links.Add(link);
        }

        return new NoteShape(links, headings, blockIds);
    }

    /// <summary>
    /// Heading and fragment comparison key, applied to BOTH sides so they can
    /// never be normalized differently.
    ///
    /// Obsidian resolves heading links case-insensitively and tolerates
    /// surrounding whitespace. NOTHING ELSE is normalized away, and that is
    /// MEASURED rather than cautious: Obsidian's own heading suggester
    /// (2026-08-30) offers <c>Target **bold** heading</c>,
    /// <c>Target `curl` heading</c> and — the one that overturned an earlier
    /// guess here — <c>Target link — [[Some Missing Note]]</c>, every one of
    /// them verbatim. The raw heading text IS the anchor, link syntax
    /// included.
    ///
    /// Stripping markup to the display text looked obviously right and was
    /// wrong twice over. It would accept <c>[[#Target bold heading]]</c>,
    /// which Obsidian does not resolve, against 12 links in this vault that
    /// correctly spell the markup out. And for a heading containing a
    /// wikilink it hides a REAL defect: the anchor contains <c>[[</c>, so no
    /// link can address it — <c>[[Note#… [[Inner]]]]</c> terminates at the
    /// first <c>]]</c> — which makes such a heading unreachable and every
    /// inbound link to it broken. Helios has one, with six inbound links.
    /// The fix belongs in the note (drop the link from the heading), not in
    /// this comparison.
    /// </summary>
    internal static string NormalizeHeading(string text)
    {
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }
            if (space && sb.Length > 0)
                sb.Append(' ');
            space = false;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>'\|' back to a literal pipe, once the separator has been decided.</summary>
    private static string Unescape(string text) =>
        text.Contains("\\|", StringComparison.Ordinal) ? text.Replace("\\|", "|", StringComparison.Ordinal) : text;

    private static IEnumerable<Ref> ScanLinks(string scannable, string raw, int lineNo, bool inTable)
    {
        for (var i = 0; i + 1 < scannable.Length; i++)
        {
            if (scannable[i] != '[' || scannable[i + 1] != '[')
                continue;
            var close = scannable.IndexOf("]]", i + 2, StringComparison.Ordinal);
            if (close < 0)
                yield break; // no terminator on this line: not a link, and nothing after it can be one
            var isEmbed = i > 0 && scannable[i - 1] == '!';
            var start = isEmbed ? i - 1 : i;
            var inner = raw.Substring(i + 2, close - (i + 2));

            var (target, fragment, isBlock, alias, unescapedPipe) = Split(inner);
            yield return new Ref(
                lineNo,
                ByteColumn(raw, start),
                raw[start..(close + 2)],
                isEmbed,
                target,
                fragment,
                isBlock,
                alias,
                unescapedPipe,
                inTable);
            i = close + 1;
        }
    }

    /// <summary>
    /// Split <c>target#fragment|alias</c>.
    ///
    /// The pipe is the whole trap, and what '\|' MEANS depends on whether the
    /// link sits in a table row. Both readings are in this vault:
    ///
    /// '\|' is ALWAYS the alias separator, escaped so a GFM table cell
    /// survives it — §8's second class, and splitting without unescaping
    /// reports a target of <c>Note\</c> which resolves to nothing (~85 junk
    /// findings on the prototype's first run). A bare '|' is the same
    /// separator and additionally sets <c>HasUnescapedPipe</c>, which is the
    /// table check's whole input.
    ///
    /// MEASURED IN OBSIDIAN, 2026-08-30, because the alternative reading is
    /// plausible enough to keep coming back: outside a table there is no
    /// table parser to consume the escape, so '\|' could have been a LITERAL
    /// pipe. It is not. A probe note rendered '[[#Target pipe | heading]]',
    /// '[[#Target pipe \| heading]]' and the escaped form inside a table row,
    /// and ALL THREE displayed the alias 'heading' — one rule, no table
    /// context. A note linking to a heading that genuinely contains a pipe is
    /// therefore broken in Obsidian too, however the pipe is spelled, and
    /// reporting it is correct.
    /// </summary>
    private static (string Target, string? Fragment, bool IsBlock, string? Alias, bool UnescapedPipe)
        Split(string inner)
    {
        var unescapedPipe = false;
        var sepIndex = -1;
        var sepLength = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] != '|')
                continue;
            var escaped = i > 0 && inner[i - 1] == '\\';
            if (!escaped)
                unescapedPipe = true;
            if (sepIndex < 0)
                (sepIndex, sepLength) = escaped ? (i - 1, 2) : (i, 1);
        }

        var head = Unescape(sepIndex < 0 ? inner : inner[..sepIndex]);
        var alias = sepIndex < 0 ? null : Unescape(inner[(sepIndex + sepLength)..]);

        var hash = head.IndexOf('#', StringComparison.Ordinal);
        if (hash < 0)
            return (head.Trim(), null, false, alias, unescapedPipe);

        var fragment = head[(hash + 1)..];
        // Obsidian nests headings as #A#B; only the deepest is checked for
        // existence, and nesting ORDER is deliberately not verified — a
        // stricter reading would invent findings this parser cannot prove.
        var lastHash = fragment.LastIndexOf('#');
        if (lastHash >= 0)
            fragment = fragment[(lastHash + 1)..];
        var isBlock = fragment.StartsWith('^');
        return (head[..hash].Trim(), isBlock ? fragment[1..].Trim() : fragment.Trim(), isBlock, alias, unescapedPipe);
    }

    /// <summary>
    /// Blank out inline code spans, preserving length so byte columns stay
    /// true. A backtick run opens a span that only a run of the SAME length
    /// closes; an unclosed run is literal text (CommonMark), so it masks
    /// nothing.
    /// </summary>
    private static string MaskInlineCode(string line)
    {
        if (!line.Contains('`', StringComparison.Ordinal))
            return line;
        var chars = line.ToCharArray();
        var i = 0;
        while (i < chars.Length)
        {
            if (chars[i] != '`')
            {
                i++;
                continue;
            }
            var open = i;
            while (i < chars.Length && chars[i] == '`')
                i++;
            var runLength = i - open;
            var close = FindRun(chars, i, runLength);
            if (close < 0)
                continue; // literal backticks; leave the rest of the line scannable
            for (var j = open; j < close + runLength; j++)
                chars[j] = ' ';
            i = close + runLength;
        }
        return new string(chars);
    }

    private static int FindRun(char[] chars, int from, int runLength)
    {
        for (var i = from; i < chars.Length; i++)
        {
            if (chars[i] != '`')
                continue;
            var start = i;
            while (i < chars.Length && chars[i] == '`')
                i++;
            if (i - start == runLength)
                return start;
        }
        return -1;
    }

    /// <summary>
    /// Mark every GFM table row: a header line whose NEXT line is a delimiter
    /// row, and the rows that follow until the block ends. Recognizing rows
    /// by "contains a pipe" alone would sweep in every shell pipeline in
    /// these notes, which is most of them.
    /// </summary>
    private static bool[] MarkTableRows(IReadOnlyList<string> lines)
    {
        var rows = new bool[lines.Count];
        for (var i = 0; i + 1 < lines.Count; i++)
        {
            if (!HasPipe(lines[i]) || !IsDelimiterRow(lines[i + 1]))
                continue;
            rows[i] = true;
            rows[i + 1] = true;
            var j = i + 2;
            while (j < lines.Count && HasPipe(lines[j]))
            {
                rows[j] = true;
                j++;
            }
            i = j - 1;
        }
        return rows;
    }

    private static bool HasPipe(string line) =>
        line.Contains('|', StringComparison.Ordinal) && Trim(line).Length > 0;

    private static bool IsDelimiterRow(string line)
    {
        var seenDash = false;
        var seenPipe = false;
        foreach (var c in line)
        {
            switch (c)
            {
                case '-': seenDash = true; break;
                case '|': seenPipe = true; break;
                case ':' or ' ' or '\t' or '\r': break;
                default: return false;
            }
        }
        return seenDash && seenPipe;
    }

    /// <summary>A line of only '=' or only '-': the setext underline.</summary>
    private static bool IsSetextUnderline(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || (trimmed[0] != '=' && trimmed[0] != '-'))
            return false;
        foreach (var c in trimmed)
        {
            if (c != trimmed[0])
                return false;
        }
        return true;
    }

    private static bool TryFence(string line, out char ch, out int length)
    {
        (ch, length) = ('\0', 0);
        var i = 0;
        while (i < line.Length && line[i] == ' ' && i < 3)
            i++;
        if (i >= line.Length || (line[i] != '`' && line[i] != '~'))
            return false;
        ch = line[i];
        while (i < line.Length && line[i] == ch)
        {
            i++;
            length++;
        }
        return length >= 3;
    }

    private static bool RestIsBlank(string line, char fence)
    {
        foreach (var c in line)
        {
            if (c != fence && c != ' ' && c != '\t' && c != '\r')
                return false;
        }
        return true;
    }

    private static bool TryHeading(string line, out string heading)
    {
        heading = "";
        var i = 0;
        while (i < line.Length && line[i] == ' ' && i < 3)
            i++;
        var hashes = 0;
        while (i < line.Length && line[i] == '#')
        {
            i++;
            hashes++;
        }
        // '##Foo' with no space is NOT a heading to CommonMark or Obsidian —
        // it renders as literal text. Treating it as one would index a
        // heading no link can ever reach.
        if (hashes is < 1 or > 6 || i >= line.Length || line[i] != ' ')
            return false;
        heading = NormalizeHeading(line[i..].TrimEnd('#').Trim());
        return heading.Length > 0;
    }

    private static bool TryBlockId(string line, out string blockId)
    {
        blockId = "";
        var end = line.TrimEnd().Length;
        var i = end - 1;
        while (i >= 0 && (char.IsLetterOrDigit(line[i]) || line[i] == '-'))
            i--;
        if (i < 0 || line[i] != '^' || i + 1 >= end)
            return false;
        if (i > 0 && !char.IsWhiteSpace(line[i - 1]))
            return false; // mid-word caret, e.g. an exponent
        blockId = line[(i + 1)..end];
        return true;
    }

    private static string Trim(string line) => line.Trim().TrimEnd('\r');

    private static int ByteColumn(string line, int charIndex) =>
        Encoding.UTF8.GetByteCount(line.AsSpan(0, charIndex)) + 1;
}
