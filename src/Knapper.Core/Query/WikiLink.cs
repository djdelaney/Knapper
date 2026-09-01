using System.Text;

namespace Knapper.Core.Query;

/// <summary>
/// The ONE wikilink parser, and the ONE place a note's link-relevant shape
/// (links, headings, block ids, table rows) is derived from its text.
///
/// It exists as a single pass because the five lint checks are not five
/// independent problems: the 22 "malformed table" findings measured on
/// Helios on 2026-08-30 are an unescaped <c>|</c> INSIDE a wikilink inside a
/// table row — a link defect wearing a table costume. A table checker written
/// beside a link checker sees the symptom and misdiagnoses the cause, and two
/// parsers would have to agree about fenced code, inline code and escaping to
/// stay consistent. One parser, five readings of its output.
///
/// The fifth reading is the table's own shape rather than a link's, and it
/// lands in the same scan one line earlier: a header row sitting directly
/// under a paragraph line is not a table at all. Obsidian absorbs it into
/// that paragraph and renders the pipes as literal text (measured in Helios
/// 2026-09-01 — 'Home/Mayapple/Projects/Screened Porch Project.md' renders
/// its Setbacks table as a wall of '|'). So the scan decides whether a
/// candidate block IS a table before marking its rows, which is also what
/// keeps the two table checks from double-reporting one defect: rows that do
/// not render as a table cannot be opening a phantom column, so table_pipe
/// stays silent inside an absorbed block and speaks up once the blank line
/// makes it a table.
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
        IReadOnlyList<string> BlockIds,
        /// <summary>Table blocks the paragraph above swallowed, in document order.</summary>
        IReadOnlyList<AbsorbedTable> AbsorbedTables);

    /// <summary>
    /// A table Obsidian does not render as one, because no blank line
    /// separates its header row from the paragraph above it.
    /// <see cref="Line"/> is the header row (1-based) — the blank line
    /// belongs above THAT line, which is why the finding is reported there
    /// rather than on the paragraph.
    /// </summary>
    internal sealed record AbsorbedTable(int Line, int Column, string Header);

    internal static NoteShape Parse(string content)
    {
        var lines = VaultReadService.SplitLines(content);
        var links = new List<Ref>();
        var headings = new List<string>();
        var blockIds = new List<string>();

        // Both masks are built in ONE forward pass each. Deciding per line by
        // walking back to the block's first row is O(n²) over a long table, on
        // a request path that carries a wall-clock budget — and these notes
        // run to 200KB.
        //
        // The fence mask is also the ONE fence definition. The table scan has
        // to know about fences as well (a ```md sample containing a table is
        // not a table, and reporting a missing blank line inside one would be
        // a finding about an example), and a second state machine beside this
        // loop's own would be free to drift from it.
        var fenced = MarkFenced(lines);
        var tables = MarkTableRows(lines, fenced);
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

            if (fenced[i])
            {
                previousParagraph = null;
                continue; // a fence delimiter or its interior: no links, no headings, no block ids
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
            var inTable = tables.Rows[i];
            foreach (var link in ScanLinks(scannable, raw, lineNo, inTable))
                links.Add(link);
        }

        return new NoteShape(links, headings, blockIds, tables.Absorbed);
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

    /// <summary>Which lines render as table rows, and which candidate blocks do not render at all.</summary>
    internal sealed record TableScan(bool[] Rows, IReadOnlyList<AbsorbedTable> Absorbed);

    /// <summary>
    /// Mark every GFM table row: a header line whose NEXT line is a delimiter
    /// row, and the rows that follow until the block ends. Recognizing rows
    /// by "contains a pipe" alone would sweep in every shell pipeline in
    /// these notes, which is most of them.
    ///
    /// A candidate block whose header row is absorbed by the paragraph above
    /// it (<see cref="AbsorbsTheNextLine"/>) is NOT marked: it is reported as
    /// an <see cref="AbsorbedTable"/> instead, and its lines stay ordinary
    /// paragraph text — which is exactly what Obsidian renders. Marking them
    /// anyway would let table_pipe report a phantom column inside a block
    /// that has no columns.
    /// </summary>
    private static TableScan MarkTableRows(IReadOnlyList<string> lines, bool[] fenced)
    {
        var rows = new bool[lines.Count];
        var absorbed = new List<AbsorbedTable>();
        for (var i = 0; i + 1 < lines.Count; i++)
        {
            if (fenced[i] || fenced[i + 1] || !HasPipe(lines[i]) || !IsDelimiterRow(lines[i + 1]))
                continue;
            var end = i + 2;
            while (end < lines.Count && !fenced[end] && HasPipe(lines[end]))
                end++;

            if (i > 0 && !fenced[i - 1] && !IsIndentedCode(lines, i) && AbsorbsTheNextLine(lines[i - 1]))
                absorbed.Add(new AbsorbedTable(i + 1, 1, Trim(lines[i])));
            else
            {
                for (var k = i; k < end; k++)
                    rows[k] = true;
            }
            // The block is consumed either way, so its own rows can never be
            // read as the header of a second table.
            i = end - 1;
        }
        return new TableScan(rows, absorbed);
    }

    /// <summary>
    /// Would a table header row on the NEXT line be swallowed by this one?
    ///
    /// A LIST ITEM absorbs it exactly as a paragraph does, and indent has
    /// nothing to do with it — measured in Obsidian 2026-09-01, after this
    /// check first shipped abstaining on the shape: both tables nested inside
    /// bullets in 'Tech/Homelab/Homelab Roadmap.md' render as walls of '|'.
    /// The bullet's text is an open paragraph and the header row is lazy
    /// continuation of it, so the fix is the same one blank line, kept at the
    /// list's indent.
    ///
    /// What is still left out is left out for precision (proposal §2 — the
    /// acceptance bar is a monitor nobody learns to ignore), and each for its
    /// own reason: a heading and a thematic break close the paragraph, so
    /// nine of Helios's twelve non-blank-preceded tables sit under one and
    /// render correctly; a table row belongs to the block already being
    /// scanned; '#' with no space after it is a paragraph to CommonMark but
    /// too close to a heading to report on; an HTML line is unmeasured. A
    /// blockquote line is unmeasured AND close to unreachable — a table whose
    /// own rows are quoted ('> |---|') is not recognized as a table here at
    /// all, so only an unquoted table directly under a quoted line would
    /// qualify.
    /// </summary>
    private static bool AbsorbsTheNextLine(string line)
    {
        var trimmed = Trim(line);
        if (trimmed.Length == 0)
            return false; // a blank line is the separator this check is about
        if (IsSetextUnderline(trimmed) || IsThematicBreak(trimmed))
            return false;
        // heading, blockquote or callout, table row, HTML block
        return trimmed[0] is not ('#' or '>' or '|' or '<');
    }

    /// <summary>
    /// Is an INDENTED candidate list content, or an indented code block?
    ///
    /// The distinction only arises once indent stopped disqualifying a
    /// candidate: inside an indented code block the pipes are code, and the
    /// line above them is code too rather than a paragraph that could absorb
    /// anything. Walk back over the indented run to the line that OPENED it —
    /// a list marker means list content (report), anything else means code
    /// (stay silent). Blank lines close neither shape, so they are stepped
    /// over.
    ///
    /// The walk also counts fence markers inside the run, because
    /// <see cref="TryFence"/> caps a fence opener at three columns of indent
    /// (CommonMark measures that against the CONTAINING block, which this
    /// parser does not track) — so a ```json block nested under a bullet, of
    /// which this vault has many, is not in the fence mask at all. An odd
    /// count above the candidate means it sits inside one, and a table
    /// written as an EXAMPLE is not a table.
    ///
    /// Bounded by the run, not by the file, and only ever entered for an
    /// indented candidate: Helios has two, both list content.
    /// </summary>
    private static bool IsIndentedCode(IReadOnlyList<string> lines, int header)
    {
        var indent = IndentOf(lines[header]);
        if (indent < 4)
            return false;
        var fences = 0;
        for (var i = header - 1; i >= 0; i--)
        {
            if (Trim(lines[i]).Length == 0)
                continue;
            if (IndentOf(lines[i]) >= indent)
            {
                if (IsFenceMarker(lines[i]))
                    fences++;
                continue; // still inside the run
            }
            return fences % 2 == 1 || !IsListMarker(Trim(lines[i]));
        }
        return true; // indented from the top of the file, with no opener at all
    }

    /// <summary>A ``` or ~~~ run at ANY indent — see <see cref="IsIndentedCode"/> for why the indent is ignored.</summary>
    private static bool IsFenceMarker(string line)
    {
        var trimmed = Trim(line);
        return trimmed.Length >= 3
            && trimmed[0] is '`' or '~'
            && trimmed[1] == trimmed[0]
            && trimmed[2] == trimmed[0];
    }

    /// <summary>Indent in COLUMNS, a tab counting as four — Obsidian indents list content with tabs.</summary>
    private static int IndentOf(string line)
    {
        var columns = 0;
        foreach (var c in line)
        {
            if (c == ' ')
                columns++;
            else if (c == '\t')
                columns += 4;
            else
                break;
        }
        return columns;
    }

    /// <summary>'***', '---' or '___' (3+ of one char, spaces allowed): a block boundary.</summary>
    private static bool IsThematicBreak(string trimmed)
    {
        var ch = '\0';
        var count = 0;
        foreach (var c in trimmed)
        {
            if (c is ' ' or '\t')
                continue;
            if (c is not ('*' or '-' or '_'))
                return false;
            if (ch == '\0')
                ch = c;
            else if (c != ch)
                return false;
            count++;
        }
        return count >= 3;
    }

    /// <summary>
    /// '- ', '* ', '+ ', '1. ' or '1) '. The space is required, so a
    /// paragraph opening with '**Bold**' is a paragraph, not a bullet —
    /// which matters for <see cref="IsIndentedCode"/>, the one caller left
    /// once a list item was found to absorb a table like any other paragraph.
    /// </summary>
    private static bool IsListMarker(string trimmed)
    {
        if (trimmed.Length >= 2 && trimmed[0] is '-' or '*' or '+' && trimmed[1] is ' ' or '\t')
            return true;
        var i = 0;
        while (i < trimmed.Length && char.IsAsciiDigit(trimmed[i]))
            i++;
        return i > 0
            && i + 1 < trimmed.Length
            && trimmed[i] is '.' or ')'
            && trimmed[i + 1] is ' ' or '\t';
    }

    /// <summary>
    /// Every line a fence opens, closes or encloses. CommonMark-ish: a fence
    /// opens with 3+ backticks or tildes and closes only on the SAME
    /// character with a run at least as long, so a ``` inside a ~~~ block
    /// closes nothing — which is why the opening char and length are both
    /// carried rather than a bool.
    /// </summary>
    private static bool[] MarkFenced(IReadOnlyList<string> lines)
    {
        var fenced = new bool[lines.Count];
        var fenceChar = '\0';
        var fenceLen = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            if (TryFence(raw, out var ch, out var len))
            {
                if (fenceChar == '\0')
                {
                    (fenceChar, fenceLen) = (ch, len);
                    fenced[i] = true;
                    continue;
                }
                if (ch == fenceChar && len >= fenceLen && RestIsBlank(raw, ch))
                {
                    (fenceChar, fenceLen) = ('\0', 0);
                    fenced[i] = true;
                    continue;
                }
            }
            fenced[i] = fenceChar != '\0';
        }
        return fenced;
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
