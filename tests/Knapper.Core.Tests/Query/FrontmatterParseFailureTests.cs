using Knapper.Core.Generation;
using Knapper.Core.Options;
using Knapper.Core.Query;
using Knapper.Core.Vault;
using YamlDotNet.Core;

namespace Knapper.Core.Tests.Query;

/// <summary>
/// Both callers of <see cref="FrontmatterYaml"/> treat a <see cref="YamlException"/>
/// as "could not examine this note" and carry on. YamlDotNet does not keep to
/// that type: an unclosed flow sequence followed by another key makes its
/// scanner throw <see cref="InvalidOperationException"/>, which escaped both
/// catches and failed vault_lint and vault_search_frontmatter for the WHOLE
/// vault with [Internal] — one note, planted by any agent or synced device,
/// and both whole-vault tools stayed down until a human found and removed it.
/// The existing broken-YAML fixture ends at the unclosed bracket, which
/// YamlDotNet does report as a YamlException, so nothing saw it; the dev
/// vault generator's Frontmatter/broken.md did.
/// </summary>
public sealed class FrontmatterParseFailureTests : IDisposable
{
    private const string Trigger = "status: [unclosed\ntags: alpha";

    private readonly TempDir _dir = new();
    private readonly VaultPathResolver _resolver;
    private readonly VaultOptions _options;
    private readonly VaultGenerationCounter _generation = new();

    public FrontmatterParseFailureTests()
    {
        _dir.File("Broken/flow-then-key.md", $"---\n{Trigger}\n---\nbody\n");
        _dir.File("Notes/Weather Station.md", "---\nstatus: active\naliases: [Tempest]\n---\n# Weather Station\n");
        _dir.File("Notes/Hub.md", "Alias: [[Tempest]].\n");
        _resolver = new VaultPathResolver(_dir.Path);
        _options = new VaultOptions { RootPath = _resolver.Root };
    }

    public void Dispose()
    {
        _generation.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public void The_trigger_surfaces_as_a_YamlException()
    {
        Should.Throw<YamlException>(() => FrontmatterYaml.Deserialize(Trigger));
    }

    [Fact]
    public void Frontmatter_search_reports_the_note_as_unparseable_and_still_answers()
    {
        var lister = new VaultFileLister(_resolver, _generation, _options, ArchivedPrefixes.None);
        var reader = new VaultReadService(_resolver, _options, _generation);
        var search = new FrontmatterSearchService(_resolver, lister, reader, _generation, _options, ArchivedPrefixes.None);

        var result = search.Search(new FrontmatterQuery { Field = "status" });

        result.Items.ShouldHaveSingleItem().Path.ShouldBe("Notes/Weather Station.md");
        result.UnparseableFiles.ShouldBe(["Broken/flow-then-key.md"]);
        result.Truncated.ShouldBeFalse();
    }

    [Fact]
    public void Lint_survives_the_note_and_keeps_resolving_aliases_elsewhere()
    {
        var lister = new VaultFileLister(_resolver, _generation, _options, ArchivedPrefixes.None);
        var reader = new VaultReadService(_resolver, _options, _generation);
        var lint = new VaultLintService(_resolver, lister, reader, _generation, _options, ArchivedPrefixes.None);

        var result = lint.Lint(new LintQuery());

        result.ScannedFiles.ShouldBe(3);
        result.Items.ShouldNotContain(f => f.Path == "Notes/Hub.md");
    }

    /// <summary>
    /// The contract is about the TYPE, for every input — not about the one
    /// input that happened to be found. Damage a corpus of ordinary
    /// frontmatter the way hand edits and interrupted syncs do (truncate it,
    /// drop a bracket or quote in) and demand that nothing but a
    /// YamlException ever comes out.
    /// </summary>
    [Fact]
    public void Malformed_frontmatter_only_ever_throws_YamlException()
    {
        string[] corpus =
        [
            "status: active\ntags: [alpha, beta]\naliases: [One, \"Two: quoted\"]",
            "title: 'single quoted'\nmeta:\n  source: invented\n  depth:\n    level: 2",
            "tags:\n  - alpha\n  - beta\nlinks:\n  - \"[[Home]]\"\n  - \"[[Project Plan]]\"",
            "nutrition: {kcal: 350, sugar_g: 28}\nwhen: 2026-09-01T10:15:00Z\ndone: false",
            "text: |\n  a literal block\n  over two lines\nnext: >\n  folded\n  text",
            "anchors: &a {x: 1}\nuse: *a\nlist: [*a, {y: 2}]",
        ];
        string[] inserts = ["[", "]", "{", "}", "\"", "'", ":", ",", "- ", "\n", "&", "*", "!", "|", "#"];

        var failures = new List<string>();
        void Probe(string yaml)
        {
            try
            {
                FrontmatterYaml.Deserialize(yaml);
            }
            catch (YamlException)
            {
            }
            catch (Exception e)
            {
                failures.Add($"{e.GetType().Name} on {System.Text.Json.JsonSerializer.Serialize(yaml)}");
            }
        }

        foreach (var doc in corpus)
        {
            for (var cut = 1; cut < doc.Length; cut++)
                Probe(doc[..cut]);
            for (var at = 0; at <= doc.Length; at++)
            {
                foreach (var ins in inserts)
                    Probe(doc.Insert(at, ins));
            }
        }

        failures.ShouldBeEmpty();
    }
}
