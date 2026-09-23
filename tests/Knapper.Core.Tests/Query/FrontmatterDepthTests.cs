using Knapper.Core.Generation;
using Knapper.Core.Options;
using Knapper.Core.Query;
using Knapper.Core.Vault;
using YamlDotNet.Core;

namespace Knapper.Core.Tests.Query;

/// <summary>
/// A deeply nested frontmatter block used to overflow YamlDotNet's recursive
/// deserializer — an uncatchable stack overflow that killed the whole process,
/// and, because lint indexes every note, killed it again on every lint after
/// the restart. If this guard regresses, these tests do not fail politely:
/// they take the test host down, which is the same symptom production had.
/// </summary>
public sealed class FrontmatterDepthTests : IDisposable
{
    // About 200 KB of note, far past the depth that overflowed at ~20 KB.
    private const int HostileDepth = 100_000;

    private readonly TempDir _dir = new();
    private readonly VaultPathResolver _resolver;
    private readonly VaultOptions _options;
    private readonly VaultGenerationCounter _generation = new();

    public FrontmatterDepthTests()
    {
        _dir.File("Hostile/flow.md", Frontmatter("a: " + new string('[', HostileDepth) + new string(']', HostileDepth)));
        _dir.File("Hostile/mapping.md", Frontmatter("a: " + string.Concat(Enumerable.Repeat("{b: ", HostileDepth)) + "1" + new string('}', HostileDepth)));
        _dir.File("Notes/Weather Station.md", Frontmatter("status: active\naliases: [Tempest]") + "# Weather Station\n");
        _dir.File("Notes/Hub.md", "Alias: [[Tempest]].\n");
        _resolver = new VaultPathResolver(_dir.Path);
        _options = new VaultOptions { RootPath = _resolver.Root };
    }

    public void Dispose() => _dir.Dispose();

    private static string Frontmatter(string yaml) => $"---\n{yaml}\n---\n";

    [Fact]
    public void Frontmatter_search_reports_a_hostile_note_as_unparseable_and_still_answers()
    {
        var lister = new VaultFileLister(_resolver, _generation, _options, ArchivedPrefixes.None);
        var reader = new VaultReadService(_resolver, _options, _generation);
        var search = new FrontmatterSearchService(_resolver, lister, reader, _generation, _options, ArchivedPrefixes.None);

        var result = search.Search(new FrontmatterQuery { Field = "status" });

        result.Items.ShouldHaveSingleItem().Path.ShouldBe("Notes/Weather Station.md");
        // Reported, not skipped: a note that could not be examined could be
        // hiding a match.
        result.UnparseableFiles.ShouldBe(["Hostile/flow.md", "Hostile/mapping.md"]);
        result.Truncated.ShouldBeFalse();
    }

    [Fact]
    public void Lint_survives_a_hostile_note_and_keeps_resolving_aliases_elsewhere()
    {
        var lister = new VaultFileLister(_resolver, _generation, _options, ArchivedPrefixes.None);
        var reader = new VaultReadService(_resolver, _options, _generation);
        var lint = new VaultLintService(_resolver, lister, reader, _generation, _options, ArchivedPrefixes.None);

        var result = lint.Lint(new LintQuery());

        result.ScannedFiles.ShouldBe(4);
        result.Items.ShouldNotContain(f => f.Path == "Notes/Hub.md");
    }

    [Fact]
    public void Nesting_is_accepted_up_to_the_limit_and_refused_one_past_it()
    {
        static string Nested(int depth) =>
            "a: " + new string('[', depth - 1) + "1" + new string(']', depth - 1);

        // The top-level mapping is one level, so depth - 1 sequences reach `depth`.
        FrontmatterYaml.Deserialize(Nested(FrontmatterYaml.MaxDepth)).ShouldNotBeNull().ShouldContainKey("a");
        Should.Throw<YamlException>(() => FrontmatterYaml.Deserialize(Nested(FrontmatterYaml.MaxDepth + 1)))
            .Message.ShouldContain("deeper than");
    }

    [Fact]
    public void Deep_block_indentation_is_refused_too()
    {
        // Block style nests by indentation instead of brackets; it reaches
        // the same recursive deserializer.
        var yaml = string.Concat(Enumerable.Range(0, FrontmatterYaml.MaxDepth + 1)
            .Select(i => new string(' ', i * 2) + $"k{i}:\n")) + new string(' ', (FrontmatterYaml.MaxDepth + 1) * 2) + "v\n";
        Should.Throw<YamlException>(() => FrontmatterYaml.Deserialize(yaml));
    }

    [Fact]
    public void An_empty_block_is_still_null_rather_than_an_error()
    {
        FrontmatterYaml.Deserialize("").ShouldBeNull();
    }
}
