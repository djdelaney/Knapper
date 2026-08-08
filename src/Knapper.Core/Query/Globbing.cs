using System.Text;
using System.Text.RegularExpressions;

namespace Knapper.Core.Query;

/// <summary>
/// rg/gitignore-style glob → regex, used by the NATIVE file lister so its
/// glob semantics match what ripgrep applies during searches (the
/// equivalence suite holds the two implementations together):
/// a pattern without '/' matches basenames at any depth; with '/' it is
/// anchored to the full relative path. '*' and '?' never cross '/',
/// '**' does, '[...]' classes ('!' negation), '{a,b}' alternation.
/// </summary>
internal static class Globbing
{
    internal static Regex Translate(string glob)
    {
        if (string.IsNullOrEmpty(glob))
            throw new KnapperException(VaultErrorCode.InvalidArgument, "glob is empty");

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
        return new Regex(sb.ToString(), RegexOptions.CultureInvariant);
    }

    /// <summary>Matches a normalized vault-relative path against a translated glob.</summary>
    internal static bool IsMatch(Regex translated, string relativePath) =>
        translated.IsMatch(relativePath);

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
