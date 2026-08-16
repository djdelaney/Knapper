using Knapper.Core.Query;

namespace Knapper.Core.Tests.Query;

public sealed class VaultSearchServiceTests : IClassFixture<FixtureVault>
{
    private readonly FixtureVault _vault;

    public VaultSearchServiceTests(FixtureVault vault) => _vault = vault;

    private static VaultSearchQuery Q(string pattern) => new() { Pattern = pattern };

    [Fact]
    public void Literal_search_finds_expected_matches_with_line_and_column()
    {
        var result = _vault.Search.SearchMatches(new VaultSearchQuery
        {
            Pattern = "TODO alpha",
            Literal = true,
            Case = CaseMode.Sensitive,
        });

        var match = result.Items.ShouldHaveSingleItem();
        match.Path.ShouldBe("Notes/Daily.md");
        match.Line.ShouldBe(2);
        match.Column.ShouldBe(1);
        match.Text.ShouldBe("TODO alpha task");
        result.Truncated.ShouldBeFalse();
        result.TotalMatches.ShouldBe(1);
        result.ScannedFiles.ShouldNotBeNull();
    }

    [Fact]
    public void Smart_case_is_insensitive_for_lowercase_and_sensitive_for_mixed()
    {
        // lowercase pattern → matches TODO, todo, and 'wrap TODO up'
        _vault.Search.SearchMatches(Q("todo")).Items.Count.ShouldBe(3);
        // uppercase in pattern → sensitive → TODO lines only
        _vault.Search.SearchMatches(Q("TODO")).Items.Count.ShouldBe(2);
    }

    [Fact]
    public void Whole_word_excludes_substring_hits()
    {
        _vault.Search.SearchMatches(new VaultSearchQuery { Pattern = "need", WholeWord = true })
            .Items.ShouldBeEmpty();
    }

    [Fact]
    public void Regex_and_multiline_work()
    {
        _vault.Search.SearchMatches(Q(@"al\w+a")).Items
            .ShouldContain(m => m.Path == "Notes/Daily.md" && m.Text.Contains("alpha"));

        var multiline = _vault.Search.SearchMatches(new VaultSearchQuery
        {
            Pattern = @"deep content\nneedle",
            Multiline = true,
        });
        var m = multiline.Items.ShouldHaveSingleItem();
        m.Path.ShouldBe("Notes/Sub/Deep.md");
        m.Line.ShouldBe(1);
    }

    [Fact]
    public void Unicode_pattern_matches_in_unicode_paths()
    {
        var result = _vault.Search.SearchMatches(Q("käse"));
        result.Items.Select(m => m.Path).Order(StringComparer.Ordinal).ShouldBe(
            ["Projects/pröject.md", "with spaces/nöte – ünïcode.md"]);
    }

    [Fact]
    public void Hidden_and_control_dirs_are_invisible_to_search()
    {
        var paths = _vault.Search.SearchMatches(Q("needle")).Items.Select(m => m.Path).Distinct().ToList();
        paths.ShouldNotContain(p => p.StartsWith('.') || p.Contains("/."));
    }

    [Fact]
    public void Binary_files_are_excluded_but_lossy_text_is_searched()
    {
        var paths = _vault.Search.SearchMatches(Q("needle")).Items.Select(m => m.Path).Distinct().ToList();
        paths.ShouldNotContain("raw/blob.bin");
        // Non-UTF-8 TEXT is still searched (rg replaces invalid bytes) —
        // exclusion is for binary, not for imperfect encodings.
        paths.ShouldContain("latin1/legacy.md");
    }

    [Fact]
    public void Prefix_and_globs_scope_the_search()
    {
        _vault.Search.SearchMatches(new VaultSearchQuery
        {
            Pattern = "needle",
            PathPrefixes = ["Notes"],
        }).Items.ShouldAllBe(m => m.Path.StartsWith("Notes/"));

        _vault.Search.SearchMatches(new VaultSearchQuery
        {
            Pattern = "needle",
            Extensions = ["sh"],
        }).Items.ShouldAllBe(m => m.Path.EndsWith(".sh"));

        _vault.Search.SearchMatches(new VaultSearchQuery
        {
            Pattern = "needle",
            IncludeGlobs = ["many/*.md"],
            ExcludeGlobs = ["*-3.md"],
        }).Items.Select(m => m.Path).Distinct().Order(StringComparer.Ordinal).ShouldBe(
            ["many/needles-0.md", "many/needles-1.md", "many/needles-2.md"]);
    }

    [Fact]
    public void Overlapping_prefixes_are_rejected()
    {
        Should.Throw<KnapperException>(() => _vault.Search.SearchMatches(new VaultSearchQuery
        {
            Pattern = "x",
            PathPrefixes = ["Notes", "Notes/Sub"],
        })).Code.ShouldBe(VaultErrorCode.InvalidArgument);
    }

    [Fact]
    public void Context_lines_are_attached()
    {
        var result = _vault.Search.SearchMatches(new VaultSearchQuery
        {
            Pattern = "Done gamma",
            ContextBefore = 2,
            ContextAfter = 1,
        });
        var match = result.Items.ShouldHaveSingleItem();
        match.ContextBefore.ShouldBe(["TODO alpha task", "todo beta task"]);
        match.ContextAfter.ShouldBe(["wrap TODO up"]);
    }

    [Fact]
    public void Sixty_matches_paginate_with_no_duplicates_no_omissions_stable_order()
    {
        var query = new VaultSearchQuery { Pattern = "needle", PathPrefixes = ["many"], MaxResults = 25 };
        var all = new List<SearchMatch>();
        string? cursor = null;
        var pages = 0;
        while (true)
        {
            var page = _vault.Search.SearchMatches(query with { Cursor = cursor });
            all.AddRange(page.Items);
            pages++;
            if (!page.Truncated)
                break;
            page.NextCursor.ShouldNotBeNull();
            cursor = page.NextCursor;
            pages.ShouldBeLessThan(10);
        }

        pages.ShouldBe(3); // 25 + 25 + 10
        all.Count.ShouldBe(60);
        all.Select(m => (m.Path, m.Line, m.Column)).Distinct().Count().ShouldBe(60);
        // Recombined pages equal the single-page result, in the same order.
        var single = _vault.Search.SearchMatches(query with { MaxResults = 200 });
        single.Items.Select(m => (m.Path, m.Line)).ShouldBe(all.Select(m => (m.Path, m.Line)));
        single.TotalMatches.ShouldBe(60);
    }

    [Fact]
    public void No_match_is_an_empty_untruncated_envelope_with_scan_evidence()
    {
        var result = _vault.Search.SearchMatches(Q("zzz_does_not_exist_zzz"));
        result.Items.ShouldBeEmpty();
        result.Truncated.ShouldBeFalse();
        result.TotalMatches.ShouldBe(0);
        // "No match" claims the scope was exhaustively searched — the summary
        // stats prove files were actually visited.
        result.ScannedFiles.ShouldNotBeNull();
        result.ScannedFiles!.Value.ShouldBeGreaterThan(5);
    }

    [Fact]
    public void Files_only_mode_returns_sorted_paths()
    {
        var result = _vault.Search.SearchFilesOnly(new VaultSearchQuery { Pattern = "needle", PathPrefixes = ["many"] });
        result.Items.ShouldBe(
            ["many/needles-0.md", "many/needles-1.md", "many/needles-2.md", "many/needles-3.md"]);
        result.Truncated.ShouldBeFalse();
    }

    [Fact]
    public void Unscoped_files_and_counts_modes_report_vault_paths_not_rg_echoes()
    {
        // WITHOUT PathPrefixes — the case the two tests above never reach.
        // rg is handed an explicit "." (it must be: given nothing it may read
        // stdin and report an empty search as an exhaustive no-match), and it
        // echoes that target back on every path it prints. The match stream
        // strips it; these two streams did not, so files/counts answered
        // "./Notes/Daily.md" — a string that is not a vault path, cannot be
        // fed back to vault_read, does not compare equal to the same file
        // from any other surface, and rides inside nextCursor as the resume
        // position.
        var files = _vault.Search.SearchFilesOnly(Q("needle"));
        files.Items.ShouldNotBeEmpty();
        files.Items.ShouldAllBe(p => !p.StartsWith("./"));
        files.Items.ShouldContain("many/needles-0.md");

        var counts = _vault.Search.SearchCounts(Q("needle"));
        counts.Items.ShouldNotBeEmpty();
        counts.Items.ShouldAllBe(c => !c.Path.StartsWith("./"));

        // The same file, spelled the same way, on every surface.
        var matches = _vault.Search.SearchMatches(Q("needle"));
        files.Items.ShouldContain(matches.Items[0].Path);
    }

    [Fact]
    public void Counts_mode_reports_per_file_and_total()
    {
        var result = _vault.Search.SearchCounts(new VaultSearchQuery { Pattern = "needle", PathPrefixes = ["many"] });
        result.Items.Count.ShouldBe(4);
        result.Items.ShouldAllBe(c => c.Count == 15);
        result.TotalMatches.ShouldBe(60);
    }

    [Fact]
    public void Invalid_regex_is_a_typed_error_with_rg_diagnostics()
    {
        var ex = Should.Throw<KnapperException>(() => _vault.Search.SearchMatches(Q("unclosed(")));
        ex.Code.ShouldBe(VaultErrorCode.InvalidArgument);
        ex.Message.ShouldContain("ripgrep rejected");
    }

    [Fact]
    public void Cursor_from_a_different_query_is_rejected()
    {
        var page = _vault.Search.SearchMatches(new VaultSearchQuery { Pattern = "needle", MaxResults = 5 });
        page.NextCursor.ShouldNotBeNull();

        Should.Throw<KnapperException>(() => _vault.Search.SearchMatches(new VaultSearchQuery
        {
            Pattern = "different",
            Cursor = page.NextCursor,
        })).Code.ShouldBe(VaultErrorCode.InvalidCursor);
    }

    [Fact]
    public void Precancelled_token_surfaces_as_cancellation_not_empty_result()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Should.Throw<OperationCanceledException>(() =>
            _vault.Search.SearchMatches(Q("needle"), cts.Token));
    }

    [Fact]
    public void Output_budget_counts_utf8_bytes_and_unicode_pages_recombine()
    {
        // The budget is MaxOutputBYTES; .NET string Length is UTF-16 code
        // units — for this content the byte count is ~2× Length, so
        // Length-based accounting would let pages carry ~2× the budget.
        using var dir = new TempDir();
        using var gen = new Knapper.Core.Generation.VaultGenerationCounter();
        var resolver = new Knapper.Core.Vault.VaultPathResolver(dir.Path);
        var line = "nëëdlë " + new string('ü', 40); // ~94 UTF-8 bytes, Length 47
        var lineBytes = System.Text.Encoding.UTF8.GetByteCount(line);
        for (var f = 0; f < 2; f++)
            dir.File($"u{f}.md", string.Join("", Enumerable.Repeat(line + "\n", 20)));

        const int Budget = 400;
        var search = new VaultSearchService(resolver, gen,
            new Knapper.Core.Options.VaultOptions { MaxOutputBytes = Budget });
        var query = new VaultSearchQuery { Pattern = "nëëdlë", MaxResults = 200 };

        var all = new List<SearchMatch>();
        string? cursor = null;
        var pages = 0;
        while (true)
        {
            var page = search.SearchMatches(query with { Cursor = cursor });
            page.Items.ShouldNotBeEmpty(); // a budgeted page always makes progress
            // The budget closes the page at the NEXT match, so one line of
            // overshoot is legal — more than that means bytes were undercounted.
            page.Items.Sum(m => System.Text.Encoding.UTF8.GetByteCount(m.Text))
                .ShouldBeLessThanOrEqualTo(Budget + lineBytes);
            all.AddRange(page.Items);
            pages++;
            pages.ShouldBeLessThan(20);
            if (!page.Truncated)
                break;
            cursor = page.NextCursor.ShouldNotBeNull();
        }

        pages.ShouldBeGreaterThan(1); // the budget actually truncated
        all.Count.ShouldBe(40);       // recombined: no duplicates, no omissions
        all.Select(m => (m.Path, m.Line, m.Column)).Distinct().Count().ShouldBe(40);
    }

    [Fact]
    public void Non_bmp_and_pua_filenames_paginate_without_omission()
    {
        // U+1F600 (emoji) is surrogate pairs in UTF-16 (D83D DE00 — sorts
        // BELOW U+E000) but F0 9F 98 80 in UTF-8 (sorts ABOVE U+E000's
        // EE 80 80). rg emits UTF-8 byte order; a UTF-16-ordinal cursor
        // filter would skip the emoji file on page 2 forever while claiming
        // truncated:false — the completeness lie this test pins shut.
        using var dir = new TempDir();
        using var gen = new Knapper.Core.Generation.VaultGenerationCounter();
        dir.File("\U0001F600 emoji.md", "needle in emoji file\n");
        dir.File(" pua.md", "needle in pua file\n");
        var search = new VaultSearchService(
            new Knapper.Core.Vault.VaultPathResolver(dir.Path), gen,
            new Knapper.Core.Options.VaultOptions());
        var query = new VaultSearchQuery { Pattern = "needle", MaxResults = 1 };

        var first = search.SearchMatches(query);
        first.Items.ShouldHaveSingleItem().Path.ShouldBe(" pua.md"); // UTF-8 order: EE.. < F0..
        first.Truncated.ShouldBeTrue();

        var second = search.SearchMatches(query with { Cursor = first.NextCursor.ShouldNotBeNull() });
        second.Items.ShouldHaveSingleItem().Path.ShouldBe("\U0001F600 emoji.md");
        second.Truncated.ShouldBeFalse(); // exhaustive — and nothing was omitted

        // The lister paginates the same names in the same order.
        var lister = new VaultFileLister(
            new Knapper.Core.Vault.VaultPathResolver(dir.Path), gen,
            new Knapper.Core.Options.VaultOptions { RootPath = dir.Path });
        var page1 = lister.List(new VaultFilesQuery { MaxResults = 1 });
        page1.Items.ShouldHaveSingleItem().Path.ShouldBe(" pua.md");
        var page2 = lister.List(new VaultFilesQuery { MaxResults = 1, Cursor = page1.NextCursor });
        page2.Items.ShouldHaveSingleItem().Path.ShouldBe("\U0001F600 emoji.md");
    }

    [Fact]
    public void Trailing_after_context_crossing_the_budget_at_eof_is_still_a_complete_result()
    {
        // After-context alone crosses the limit and nothing follows: the
        // scope WAS exhaustively searched, so the result must be complete
        // (not truncated) — merely at its bounded one-record overshoot.
        using var dir = new TempDir();
        using var gen = new Knapper.Core.Generation.VaultGenerationCounter();
        var filler = new string('x', 120);
        dir.File("only.md", $"needle omega\n{filler}\n{filler}\n{filler}\n");

        var search = new VaultSearchService(
            new Knapper.Core.Vault.VaultPathResolver(dir.Path), gen,
            new Knapper.Core.Options.VaultOptions { MaxOutputBytes = 200 });
        var page = search.SearchMatches(new VaultSearchQuery { Pattern = "needle", ContextAfter = 3 });

        page.Truncated.ShouldBeFalse();
        page.NextCursor.ShouldBeNull();
        var match = page.Items.ShouldHaveSingleItem();
        match.ContextAfter.ShouldNotBeNull().Count.ShouldBe(3); // context is delivered, not dropped
    }

    [Fact]
    public void After_context_crossing_the_budget_closes_the_page_at_the_next_match()
    {
        // The budget is noticed on the context DELIVERY, so the next match
        // closes the page instead of being admitted first — and the pages
        // recombine losslessly from the cursor.
        using var dir = new TempDir();
        using var gen = new Knapper.Core.Generation.VaultGenerationCounter();
        var filler = new string('x', 120);
        dir.File("two.md", $"needle alpha\n{filler}\n{filler}\n{filler}\nquiet\nneedle beta\ntail\n");

        var search = new VaultSearchService(
            new Knapper.Core.Vault.VaultPathResolver(dir.Path), gen,
            new Knapper.Core.Options.VaultOptions { MaxOutputBytes = 200 });
        var query = new VaultSearchQuery { Pattern = "needle", ContextAfter = 3 };

        var first = search.SearchMatches(query);
        first.Items.ShouldHaveSingleItem().Text.ShouldBe("needle alpha");
        first.Truncated.ShouldBeTrue();

        var second = search.SearchMatches(query with { Cursor = first.NextCursor.ShouldNotBeNull() });
        second.Items.Select(m => m.Text).ShouldBe(["needle beta"]);
        second.Truncated.ShouldBeFalse();
    }

    [Fact]
    public void Oversize_cursor_and_prefix_flood_are_typed_refusals()
    {
        Should.Throw<KnapperException>(() => _vault.Search.SearchMatches(
            Q("needle") with { Cursor = new string('A', 5000) }))
            .Code.ShouldBe(VaultErrorCode.InvalidCursor);

        Should.Throw<KnapperException>(() => _vault.Search.SearchMatches(
            Q("needle") with { PathPrefixes = [.. Enumerable.Range(0, 65).Select(i => $"p{i}")] }))
            .Code.ShouldBe(VaultErrorCode.InvalidArgument);
    }

    [Fact]
    public void Missing_ripgrep_binary_is_a_typed_error_with_a_hint()
    {
        var broken = new VaultSearchService(_vault.Resolver, _vault.Generation,
            new Knapper.Core.Options.VaultOptions { RipgrepPath = "/nonexistent/rg" });
        var ex = Should.Throw<KnapperException>(() => broken.SearchMatches(Q("x")));
        ex.Code.ShouldBe(VaultErrorCode.IoError);
        ex.Message.ShouldContain("is ripgrep installed");
    }

    [Fact]
    public void A_mutation_during_the_query_is_reported()
    {
        var service = new VaultSearchService(_vault.Resolver, _vault.Generation, _vault.Options)
        {
            OnQueryStarted = () => _vault.Generation.Increment(),
        };
        var result = service.SearchMatches(Q("needle"));
        result.ChangedDuringQuery.ShouldBeTrue();
        result.GenerationEnd.ShouldBe(result.GenerationStart + 1);

        // ...and a quiet query reports stability.
        _vault.Search.SearchMatches(Q("needle")).ChangedDuringQuery.ShouldBeFalse();
    }
}
