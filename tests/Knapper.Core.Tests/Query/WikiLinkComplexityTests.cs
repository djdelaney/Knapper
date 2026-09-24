using System.Diagnostics;
using System.Text;
using Knapper.Core.Query;

namespace Knapper.Core.Tests.Query;

/// <summary>
/// Lint parses every note, and its deadline is only checked BETWEEN notes,
/// so one note an agent can write must never cost more than linear time.
/// Two paths were quadratic: each link's byte column was recounted from the
/// start of its line (a 1 MB line of [[a]] took 2.6 s in that loop alone,
/// about 40 s at the 4 MB read cap), and each unmatched backtick run
/// rescanned to the end of the line.
/// </summary>
public sealed class WikiLinkComplexityTests
{
    // Generous for a slow CI box, and still far under the old cost: measured
    // against the pre-fix parser, the link line took 56 s and the backtick
    // line (at 2,000 runs) 6 s — this one is sized up so it clearly fails too.
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Fact]
    public void Byte_columns_stay_exact_after_multibyte_text()
    {
        // "é " is 3 bytes → col 4. "[[a]] 日本 " adds 5 + 1 + 6 + 1 → col 17.
        // "[[b]] 😀" adds 5 + 1 + 4 → the embed's '!' is col 27.
        var links = WikiLink.Parse("é [[a]] 日本 [[b]] 😀![[c]]\n").Links;
        links.Select(l => l.Column).ShouldBe([4, 17, 27]);
    }

    [Fact]
    public void A_two_megabyte_line_of_links_parses_in_linear_time()
    {
        var line = string.Concat(Enumerable.Repeat("é[[a]]", 350_000)) + "\n";
        var watch = Stopwatch.StartNew();
        var links = WikiLink.Parse(line).Links;
        watch.Elapsed.ShouldBeLessThan(Budget);
        links.Count.ShouldBe(350_000);
        // The last column must still be exact: 350k × ("é" 2 bytes + "[[a]]" 5 bytes).
        links[^1].Column.ShouldBe(349_999 * 7 + 3);
    }

    [Fact]
    public void A_line_of_unmatched_backtick_runs_masks_in_linear_time()
    {
        // Runs of strictly increasing length never find a closer, so the old
        // search scanned to the end of the line once per run.
        var sb = new StringBuilder();
        for (var n = 1; n <= 3_000; n++)
            sb.Append('`', n).Append('a');
        var line = sb.ToString();
        var watch = Stopwatch.StartNew();
        WikiLink.MaskInlineCode(line).ShouldBe(line);
        watch.Elapsed.ShouldBeLessThan(Budget);
    }

    /// <summary>The replacement must mask exactly what the original did.</summary>
    [Fact]
    public void Masking_agrees_with_the_original_algorithm_on_random_lines()
    {
        var random = new Random(20260924);
        const string Alphabet = "``` a[]";
        for (var trial = 0; trial < 20_000; trial++)
        {
            var chars = new char[random.Next(0, 40)];
            for (var i = 0; i < chars.Length; i++)
                chars[i] = Alphabet[random.Next(Alphabet.Length)];
            var line = new string(chars);
            WikiLink.MaskInlineCode(line).ShouldBe(ReferenceMask(line), $"input: {line}");
        }
    }

    /// <summary>The pre-fix algorithm, verbatim in behaviour: correct, but quadratic.</summary>
    private static string ReferenceMask(string line)
    {
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
            var close = -1;
            for (var k = i; k < chars.Length; k++)
            {
                if (chars[k] != '`')
                    continue;
                var start = k;
                while (k < chars.Length && chars[k] == '`')
                    k++;
                if (k - start == runLength)
                {
                    close = start;
                    break;
                }
            }
            if (close < 0)
                continue;
            for (var j = open; j < close + runLength; j++)
                chars[j] = ' ';
            i = close + runLength;
        }
        return new string(chars);
    }
}
