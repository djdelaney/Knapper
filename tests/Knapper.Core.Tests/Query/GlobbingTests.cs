using Knapper.Core.Query;

namespace Knapper.Core.Tests.Query;

public sealed class GlobbingTests
{
    private static bool Match(string glob, string path) =>
        Globbing.IsMatch(Globbing.Translate(glob), path);

    [Theory]
    // No '/' → basename at any depth (the rg/gitignore rule)
    [InlineData("*.md", "a.md", true)]
    [InlineData("*.md", "deep/nested/a.md", true)]
    [InlineData("*.md", "a.md.bak", false)]
    [InlineData("*.md", "amd", false)]
    // '*' never crosses '/'
    [InlineData("a/*.md", "a/x.md", true)]
    [InlineData("a/*.md", "a/b/x.md", false)]
    // '**' crosses
    [InlineData("a/**/z.md", "a/z.md", true)]
    [InlineData("a/**/z.md", "a/b/z.md", true)]
    [InlineData("a/**/z.md", "a/b/c/z.md", true)]
    [InlineData("a/**", "a/anything/deep.md", true)]
    [InlineData("a/**", "b/a/x.md", false)]
    [InlineData("**/z.md", "z.md", true)]
    [InlineData("**/z.md", "x/y/z.md", true)]
    // '?' single non-slash
    [InlineData("a?c.md", "abc.md", true)]
    [InlineData("a?c.md", "a/c.md", false)]
    // classes and alternation
    [InlineData("needles-[01].md", "needles-0.md", true)]
    [InlineData("needles-[01].md", "needles-2.md", false)]
    [InlineData("needles-[!01].md", "needles-2.md", true)]
    [InlineData("*.{md,sh}", "a.sh", true)]
    [InlineData("*.{md,sh}", "a.py", false)]
    // anchored full-path patterns don't float
    [InlineData("a/b.md", "a/b.md", true)]
    [InlineData("a/b.md", "x/a/b.md", false)]
    // regex metacharacters in names are literal
    [InlineData("a+b.md", "a+b.md", true)]
    [InlineData("a+b.md", "aab.md", false)]
    public void Glob_semantics_match_rg(string glob, string path, bool expected) =>
        Match(glob, path).ShouldBe(expected);

    [Theory]
    [InlineData("")]
    [InlineData("a[bc.md")]
    [InlineData("a{b,c.md")]
    public void Malformed_globs_are_typed_errors(string glob) =>
        Should.Throw<KnapperException>(() => Globbing.Translate(glob))
            .Code.ShouldBe(VaultErrorCode.InvalidArgument);
}
