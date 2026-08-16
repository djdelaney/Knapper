using Knapper.Core.Query;

namespace Knapper.Core.Tests.Query;

public sealed class FrontmatterSearchTests : IClassFixture<FixtureVault>
{
    private readonly FixtureVault _vault;

    public FrontmatterSearchTests(FixtureVault vault) => _vault = vault;

    [Fact]
    public void Exists_finds_fields_and_reports_unparseable_files()
    {
        var result = _vault.Frontmatter.Search(new FrontmatterQuery { Field = "status" });
        result.Items.Select(m => m.Path).ShouldBe(["fm/a.md", "fm/b.md"]);
        // Anything whose frontmatter could not be examined is REPORTED — it
        // could be hiding a match: broken YAML, an unterminated fence, and
        // equally a non-UTF-8 .md whose text can't even be decoded.
        result.UnparseableFiles.ShouldBe(["fm/broken.md", "fm/unterminated.md", "latin1/legacy.md"]);
        result.Truncated.ShouldBeFalse();
        result.ScannedFiles.ShouldNotBeNull();
    }

    [Fact]
    public void Equals_is_case_insensitive_and_covers_list_elements()
    {
        _vault.Frontmatter.Search(new FrontmatterQuery
        {
            Field = "status", Op = FrontmatterOp.Equals, Value = "ARCHIVED",
        }).Items.ShouldHaveSingleItem().Path.ShouldBe("fm/b.md");

        var listHit = _vault.Frontmatter.Search(new FrontmatterQuery
        {
            Field = "tags", Op = FrontmatterOp.Equals, Value = "beta",
        }).Items.ShouldHaveSingleItem();
        listHit.Path.ShouldBe("fm/a.md");
        listHit.Value.ShouldBe("alpha, beta");
    }

    [Fact]
    public void Contains_matches_substrings()
    {
        _vault.Frontmatter.Search(new FrontmatterQuery
        {
            Field = "title", Op = FrontmatterOp.Contains, Value = "note",
        }).Items.ShouldHaveSingleItem().Path.ShouldBe("fm/b.md");

        _vault.Frontmatter.Search(new FrontmatterQuery
        {
            Field = "title", Op = FrontmatterOp.Contains, Value = "zzz",
        }).Items.ShouldBeEmpty();
    }

    [Fact]
    public void Value_ops_require_a_value()
    {
        Should.Throw<KnapperException>(() => _vault.Frontmatter.Search(new FrontmatterQuery
        {
            Field = "status", Op = FrontmatterOp.Equals,
        })).Code.ShouldBe(VaultErrorCode.InvalidArgument);
    }

    [Fact]
    public void Pagination_recombines_and_respects_prefix()
    {
        var query = new FrontmatterQuery { Field = "status", PathPrefix = "fm", MaxResults = 1 };
        var all = new List<string>();
        string? cursor = null;
        while (true)
        {
            var page = _vault.Frontmatter.Search(query with { Cursor = cursor });
            all.AddRange(page.Items.Select(m => m.Path));
            if (!page.Truncated)
                break;
            cursor = page.NextCursor.ShouldNotBeNull();
        }
        all.ShouldBe(["fm/a.md", "fm/b.md"]);
    }

    [Fact]
    public void Frontmatter_block_extraction_is_strict()
    {
        FrontmatterSearchService.ExtractFrontmatterBlock("---\na: 1\n---\nbody")
            .ShouldBe((FrontmatterSearchService.FrontmatterShape.Present, "a: 1"));
        FrontmatterSearchService.ExtractFrontmatterBlock("---\na: 1\n...\n")
            .ShouldBe((FrontmatterSearchService.FrontmatterShape.Present, "a: 1"));
        FrontmatterSearchService.ExtractFrontmatterBlock("no fences")
            .ShouldBe((FrontmatterSearchService.FrontmatterShape.None, null));
        FrontmatterSearchService.ExtractFrontmatterBlock("\n---\na: 1\n---\n")
            .ShouldBe((FrontmatterSearchService.FrontmatterShape.None, null)); // must be line 1
        // An opening fence with no close is MALFORMED, not absent — it could
        // be hiding a match and must reach UnparseableFiles.
        FrontmatterSearchService.ExtractFrontmatterBlock("---\nunterminated: yes\n")
            .ShouldBe((FrontmatterSearchService.FrontmatterShape.Malformed, null));
        FrontmatterSearchService.ExtractFrontmatterBlock("")
            .ShouldBe((FrontmatterSearchService.FrontmatterShape.None, null));
    }
}
