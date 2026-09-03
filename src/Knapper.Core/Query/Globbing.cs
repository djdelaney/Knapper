using System.Text;
using System.Text.RegularExpressions;

namespace Knapper.Core.Query;

/// <summary>
/// rg/gitignore-style glob → regex, used by the NATIVE file lister so its
/// glob semantics match what ripgrep applies during searches (the
/// equivalence suite holds the two implementations together):
/// a pattern without '/' matches basenames at any depth; with '/' it is
/// anchored to the full relative path. '*' and '?' never cross '/',
/// '**' does, '[...]' classes ('!' negation INSIDE a class), '{a,b}'
/// alternation. A leading '!' on the whole pattern is REFUSED, not honored
/// — see <see cref="Validate"/>.
/// </summary>
internal static class Globbing
{
    /// <summary>Bounds on agent-supplied globs — generous for real use, hostile to regex bombs.</summary>
    private const int MaxGlobLength = 256;
    private const int MaxWildcards = 32;

    /// <summary>
    /// The ONE precondition every agent-supplied glob passes, on BOTH
    /// surfaces — the native lister via <see cref="Translate"/> and
    /// <c>vault_search</c>'s include/exclude lists, which never reach
    /// <see cref="Translate"/> because rg does its own matching. Two
    /// validators would be free to drift, and a glob that means different
    /// things on the two surfaces is the exact defect this closes.
    ///
    /// A LEADING '!' is refused rather than honored. rg's own --glob spells
    /// exclusion that way, and both tool descriptions say "rg-style glob",
    /// so agents write it — but the two surfaces disagreed about what it
    /// meant. `vault_search` handed it to rg, which excluded (measured, rg
    /// 15.2.0); the lister translated it as a literal '!' and matched
    /// nothing, answering with an exhaustive-looking `totalMatches: 0` — a
    /// confidently empty result to a question the caller never asked. And
    /// `excludeGlobs: ["!x"]` became `--glob=!!x`, which excludes only names
    /// literally starting with '!'. Exclusion is expressed STRUCTURALLY here
    /// (`excludeGlobs`), so a leading '!' is redundant where it worked and
    /// silently wrong everywhere else; refusing it is the only answer that
    /// makes both surfaces agree.
    /// </summary>
    internal static void Validate(string glob)
    {
        if (string.IsNullOrWhiteSpace(glob) || glob.Contains('\0'))
            throw new KnapperException(VaultErrorCode.InvalidArgument, "glob is empty or contains NUL");
        if (glob[0] == '!')
        {
            throw new KnapperException(VaultErrorCode.InvalidArgument,
                $"a leading '!' is not negation here: {glob} — vault_search spells exclusion with " +
                "exclude_globs (pass the pattern WITHOUT the '!'), and vault_files has no exclusion " +
                "filter, so narrow it positively with glob, extensions or path_prefix");
        }
    }

    internal static Regex Translate(string glob)
    {
        Validate(glob);
        // This is .NET regex, not rg's linear-time engine: stacked '**' and
        // '{...}' alternations translate to nested unbounded quantifiers,
        // and a backtracking match against a long failing path goes
        // super-polynomial — an agent-suppliable request-thread hang. The
        // complexity cap bounds construction cost, NonBacktracking (below)
        // makes matching linear-time like rg's own engine.
        var wildcards = glob.Count(ch => ch is '*' or '?' or '{');
        if (glob.Length > MaxGlobLength || wildcards > MaxWildcards)
        {
            throw new KnapperException(VaultErrorCode.InvalidArgument,
                $"glob is too complex ({glob.Length} chars, {wildcards} wildcards — " +
                $"caps are {MaxGlobLength} and {MaxWildcards})");
        }

        var sb = new StringBuilder(glob.Contains('/') ? "^" : "(?:^|/)");
        var braceDepth = 0;
        var i = 0;
        while (i < glob.Length)
        {
            var c = glob[i];
            switch (c)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    if (i + 2 < glob.Length && glob[i + 2] == '/')
                    {
                        sb.Append("(?:.*/)?"); // "a/**/b" also matches "a/b"
                        i += 3;
                    }
                    else
                    {
                        sb.Append(".*");
                        i += 2;
                    }
                    break;
                case '*':
                    sb.Append("[^/]*");
                    i++;
                    break;
                case '?':
                    sb.Append("[^/]");
                    i++;
                    break;
                case '[':
                    i = TranslateClass(glob, i, sb);
                    break;
                case '{':
                    braceDepth++;
                    sb.Append("(?:");
                    i++;
                    break;
                case '}' when braceDepth > 0:
                    braceDepth--;
                    sb.Append(')');
                    i++;
                    break;
                case ',' when braceDepth > 0:
                    sb.Append('|');
                    i++;
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    i++;
                    break;
            }
        }
        if (braceDepth != 0)
            throw new KnapperException(VaultErrorCode.InvalidArgument, $"unbalanced '{{' in glob: {glob}");
        sb.Append('$');
        // NonBacktracking = linear-time matching (the guarantee rg's Rust
        // regex gives); the timeout is belt-and-suspenders should the
        // pattern ever pick up a construct that forces the backtracking
        // engine. Both must stay.
        return new Regex(sb.ToString(),
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
            TimeSpan.FromMilliseconds(250));
    }

    /// <summary>Matches a normalized vault-relative path against a translated glob.</summary>
    internal static bool IsMatch(Regex translated, string relativePath)
    {
        try
        {
            return translated.IsMatch(relativePath);
        }
        catch (RegexMatchTimeoutException)
        {
            throw new KnapperException(VaultErrorCode.InvalidArgument,
                "glob match timed out — the pattern is pathological; simplify it");
        }
    }

    private static int TranslateClass(string glob, int start, StringBuilder sb)
    {
        var end = glob.IndexOf(']', start + 1);
        // "[]abc]" — a ']' first in the class is literal.
        if (end == start + 1 || (end == start + 2 && glob[start + 1] == '!'))
            end = glob.IndexOf(']', end + 1);
        if (end < 0)
            throw new KnapperException(VaultErrorCode.InvalidArgument, $"unbalanced '[' in glob: {glob}");

        var body = glob[(start + 1)..end];
        if (body.StartsWith('!'))
            body = "^" + body[1..];
        // '\' has no special meaning in our globs; escape it so it can't
        // mutate the regex class. Everything else ([-, ^ position, ranges)
        // carries regex class semantics already.
        sb.Append('[').Append(body.Replace("\\", "\\\\")).Append(']');
        return end + 1;
    }
}
