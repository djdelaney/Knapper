using Knapper.Core.Generation;
using Knapper.Core.Options;
using Knapper.Core.Query;
using Knapper.Core.Vault;

namespace Knapper.Core.Tests.Query;

/// <summary>
/// <c>Vault:ArchivedPrefixes</c> on the read surfaces. The property under test
/// is not "archived files are hidden" — it is that hiding them never makes a
/// response LIE. <c>truncated == false</c> means "this scope was searched
/// exhaustively", so a server-side exclusion narrows the scope and the
/// envelope has to say which subtrees it skipped; otherwise an agent that
/// finds nothing cannot tell "no such note" from "not where I looked".
/// </summary>
public sealed class ArchivedScopeTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly VaultGenerationCounter _generation = new();
    private readonly VaultPathResolver _resolver;
    private readonly VaultOptions _options;
    private readonly ArchivedPrefixes _archived = new(["Archive"]);
    private readonly VaultSearchService _search;
    private readonly VaultFileLister _lister;
    private readonly FrontmatterSearchService _frontmatter;

    public ArchivedScopeTests()
    {
        _dir.File("Notes/Live.md", "---\nstatus: current\n---\nneedle in a live note\n");
        _dir.File("Archive/Old.md", "---\nstatus: current\n---\nneedle in an archived note\n");
        _dir.File("Archive/2024/Older.md", "needle deep in the archive\n");
        // The sibling that a bare StartsWith would swallow.
        _dir.File("Archived Recipes/Pie.md", "needle in a folder that only LOOKS archived\n");

        _resolver = new VaultPathResolver(_dir.Path);
        _options = new VaultOptions { RootPath = _resolver.Root };
        _search = new VaultSearchService(_resolver, _generation, _options, _archived);
        _lister = new VaultFileLister(_resolver, _generation, _options, _archived);
        var reader = new VaultReadService(_resolver, _options, _generation);
        _frontmatter = new FrontmatterSearchService(_resolver, _lister, reader, _generation, _options, _archived);
    }

    [Fact]
    public void An_unscoped_search_withholds_archived_matches_and_names_what_it_skipped()
    {
        var result = _search.SearchMatches(new VaultSearchQuery { Pattern = "needle" });

        result.Items.Select(m => m.Path).ShouldBe(["Archived Recipes/Pie.md", "Notes/Live.md"], ignoreOrder: true);
        result.Truncated.ShouldBeFalse();
        // The declaration is the whole point: exhaustive OVER A NAMED SCOPE.
        result.ExcludedPrefixes.ShouldBe(["Archive"]);
    }

    [Fact]
    public void An_extension_filter_does_not_smuggle_archived_files_back_in()
    {
        // THE case the ordinal post-filter exists for. `extensions` becomes an
        // --iglob WHITELIST, and an --iglob include beats the --glob exclusion
        // the planner adds — the same override asymmetry that once let
        // `--glob=*.md` return dot-files. So rg really does hand these back,
        // and only the filter on the emitted path withholds them. Without it
        // this test returns Archive/Old.md while the unfiltered search above
        // stays green, which is exactly how the dot-file defect shipped.
        var result = _search.SearchMatches(new VaultSearchQuery { Pattern = "needle", Extensions = ["md"] });

        result.Items.Select(m => m.Path).ShouldNotContain("Archive/Old.md");
        result.Items.Select(m => m.Path).ShouldBe(["Archived Recipes/Pie.md", "Notes/Live.md"], ignoreOrder: true);
        result.ExcludedPrefixes.ShouldBe(["Archive"]);
    }

    [Fact]
    public void Every_search_mode_excludes_alike()
    {
        // Three separate stream parsers, three separate chances to forget.
        var files = _search.SearchFilesOnly(new VaultSearchQuery { Pattern = "needle" });
        files.Items.ShouldNotContain("Archive/Old.md");
        files.ExcludedPrefixes.ShouldBe(["Archive"]);

        var counts = _search.SearchCounts(new VaultSearchQuery { Pattern = "needle" });
        counts.Items.Select(c => c.Path).ShouldNotContain("Archive/Old.md");
        counts.ExcludedPrefixes.ShouldBe(["Archive"]);
        // A withheld record must not be counted as seen either.
        counts.TotalMatches.ShouldBe(2);
    }

    [Fact]
    public void Naming_the_archived_prefix_reaches_it_and_declares_no_exclusion()
    {
        var result = _search.SearchMatches(
            new VaultSearchQuery { Pattern = "needle", PathPrefixes = ["Archive"] });

        result.Items.Select(m => m.Path)
            .ShouldBe(["Archive/2024/Older.md", "Archive/Old.md"], ignoreOrder: true);
        result.ExcludedPrefixes.ShouldBeEmpty();

        var listed = _lister.List(new VaultFilesQuery { PathPrefix = "Archive", Kind = EntryKind.File });
        listed.Items.Select(i => i.Path).ShouldContain("Archive/Old.md");
        listed.ExcludedPrefixes.ShouldBeEmpty();
    }

    [Fact]
    public void The_lister_and_ripgrep_agree_about_what_exists()
    {
        // The differential that matters: two implementations of "hidden",
        // one native and one rg. They have disagreed before (dot-files under
        // an include glob), and a disagreement here is invisible from either
        // side alone — each surface stays internally consistent and green.
        var listed = _lister.List(new VaultFilesQuery { Kind = EntryKind.File })
            .Items.Select(i => i.Path).Order(StringComparer.Ordinal).ToList();
        var searched = _search.SearchFilesOnly(new VaultSearchQuery { Pattern = "needle" })
            .Items.Order(StringComparer.Ordinal).ToList();

        listed.ShouldBe(searched);
        listed.ShouldNotContain("Archive/Old.md");
        listed.ShouldContain("Archived Recipes/Pie.md"); // the sibling survives on BOTH surfaces
    }

    [Fact]
    public void The_listers_scanned_count_describes_the_scope_it_reports_on()
    {
        var listed = _lister.List(new VaultFilesQuery { Kind = EntryKind.File });

        // scannedFiles is the evidence behind "exhaustively searched"; counting
        // files this listing then withholds would inflate it.
        listed.ScannedFiles.ShouldBe(2);
        listed.TotalMatches.ShouldBe(2);
    }

    [Fact]
    public void Frontmatter_search_skips_archived_notes_and_says_so()
    {
        var result = _frontmatter.Search(new FrontmatterQuery
        {
            Field = "status",
            Op = FrontmatterOp.Equals,
            Value = "current",
        });

        result.Items.Select(m => m.Path).ShouldBe(["Notes/Live.md"]);
        result.ExcludedPrefixes.ShouldBe(["Archive"]);
        // The two archived notes were never opened, so they are not counted
        // as examined: Notes/Live.md and Archived Recipes/Pie.md are.
        result.ScannedFiles.ShouldBe(2);
    }

    public void Dispose()
    {
        _generation.Dispose();
        _dir.Dispose();
    }
}
