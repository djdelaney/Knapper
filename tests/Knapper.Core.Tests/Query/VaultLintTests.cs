using Knapper.Core.Query;

namespace Knapper.Core.Tests.Query;

public sealed class VaultLintTests : IClassFixture<LintFixtureVault>
{
    private readonly LintFixtureVault _vault;

    public VaultLintTests(LintFixtureVault vault) => _vault = vault;

    private IReadOnlyList<LintFinding> Findings(string check, string? prefix = null) =>
        [.. _vault.Lint.Lint(new LintQuery { PathPrefix = prefix, Checks = [check] }).Items];

    [Fact]
    public void Unresolved_links_are_reported_and_valid_ones_are_not()
    {
        Findings(LintChecks.UnresolvedLink, "Notes").Select(f => f.Subject).ShouldBe(
        [
            // An incomplete path is NOT resolved by suffix: the file is under
            // Tech/, and the pass that measured this vault called it broken.
            "Home Assistant/InfluxDB Migration Plan",
            "La-Z-Boy",
        ]);
    }

    [Fact]
    public void Attachments_aliases_and_same_file_anchors_all_resolve()
    {
        // The measured pass treated pg-dump-backup.sh as a valid target:
        // resolution is against every vault file, not the .md subset.
        var subjects = Findings(LintChecks.UnresolvedLink, "Notes").Select(f => f.Subject).ToList();
        subjects.ShouldNotContain("pg-dump-backup.sh");
        subjects.ShouldNotContain("Tempest");   // a frontmatter alias
        subjects.ShouldNotContain("#Hub");      // a heading in the linking file itself
    }

    [Fact]
    public void An_embed_of_an_absent_attachment_is_never_reported()
    {
        // Not merely §8's "an attachment is not a missing note": §11's size
        // ceiling is symmetric, so an over-limit attachment never reaches this
        // replica and would produce a confident false finding.
        Findings(LintChecks.UnresolvedLink, "Notes")
            .ShouldNotContain(f => f.Subject.Contains("missing-diagram", StringComparison.Ordinal));
    }

    [Fact]
    public void A_renamed_heading_breaks_its_inbound_anchors()
    {
        // The live case: a heading gained "(historical — single Crucial P3
        // Plus)" and five inbound links went stale at once.
        Findings(LintChecks.BrokenAnchor, "Notes").Select(f => f.Subject).ShouldBe(
        [
            "Mailvec Stack#Backups",
            "Windows Utility VM#Measured throughput — 2026-08-11",
            // A same-file [[#Heading]] resolves to its own note, so a missing
            // one is a broken ANCHOR — never an unresolved link.
            "#Nope",
        ]);
    }

    [Fact]
    public void An_unreadable_target_suppresses_anchor_findings_and_is_reported_instead()
    {
        var result = _vault.Lint.Lint(new LintQuery { PathPrefix = "Notes" });
        // The file EXISTS, so the link resolves...
        result.Items.ShouldNotContain(f => f.Subject.StartsWith("legacy#", StringComparison.Ordinal));
        // ...but its headings are unknown, and the silence is accounted for.
        result.UnexaminedFiles.ShouldBe(["legacy.md"]);
    }

    [Fact]
    public void A_basename_matching_two_notes_is_ambiguous_rather_than_resolved()
    {
        var finding = Findings(LintChecks.AmbiguousLink, "Notes").ShouldHaveSingleItem();
        finding.Subject.ShouldBe("Cabinets");
        finding.Message.ShouldContain("Kitchen/Cabinets.md");
        finding.Message.ShouldContain("Laundry/Cabinets.md");
    }

    [Fact]
    public void Only_an_unescaped_pipe_inside_a_table_row_is_flagged()
    {
        var finding = Findings(LintChecks.TablePipe, "Notes").ShouldHaveSingleItem();
        finding.Subject.ShouldBe("[[Mailvec Stack|MS]]");
        finding.Line.ShouldBe(16);
    }

    [Fact]
    public void A_table_the_paragraph_above_it_absorbs_is_reported_and_nothing_else_is()
    {
        // The live cases, measured in Helios 2026-09-01: 'Screened Porch
        // Project.md' renders its whole Setbacks table as literal pipes
        // because the paragraph above it has no blank line under it, and both
        // tables nested inside bullets in 'Tech/Homelab/Homelab Roadmap.md'
        // do the same — a bullet's text is an open paragraph like any other,
        // whatever the indent.
        //
        // The set is exact on purpose, because everything ELSE in
        // Tables/Absorbed.md is a shape that must stay silent: a header row
        // under an ATX heading (9 of the 12 non-blank-preceded tables in that
        // sweep, all rendering correctly), one with its blank line already
        // present, one inside an indented code block, where the pipes are
        // code, and one inside a ```md fence, which is an example.
        Findings(LintChecks.TableNeedsBlankLine, "Tables")
            .Select(f => (f.Path, f.Line, f.Subject))
            .ShouldBe(
            [
                ("Tables/Absorbed.md", 3, "| Yard | Minimum |"),
                ("Tables/Absorbed.md", 17, "| Tag | Nodes |"),
                ("Tables/Absorbed.md", 21, "| Tag | Nodes |"),
                ("Tables/Pipe.md", 2, "| note | why |"),
            ]);
    }

    [Fact]
    public void An_absorbed_block_is_not_a_table_row_so_table_pipe_stays_silent_inside_it()
    {
        // One defect, one finding. The pipe in [[Mailvec Stack|MS]] opens no
        // column while the block renders as prose; it becomes a table_pipe
        // finding once the blank line above turns the block into a table.
        Findings(LintChecks.TablePipe, "Tables").ShouldBeEmpty();
        // And the link is still a link: it resolves, absorbed or not.
        Findings(LintChecks.UnresolvedLink, "Tables").ShouldBeEmpty();
    }

    [Fact]
    public void Findings_about_one_file_are_ordered_by_position_whatever_produced_them()
    {
        // Links are emitted in document order and tables in their own, so
        // appending rather than merging would put a finding behind one the
        // page cursor had already passed — and the next page would skip it.
        var all = _vault.Lint.Lint(new LintQuery()).Items;
        foreach (var group in all.GroupBy(f => f.Path))
        {
            group.Select(f => (f.Line, f.Column))
                .ShouldBe(group.Select(f => (f.Line, f.Column)).OrderBy(p => p.Line).ThenBy(p => p.Column));
        }
    }

    [Fact]
    public void The_index_is_whole_vault_even_when_reporting_is_scoped()
    {
        // Notes/Hub.md links into Tech/ and scripts/; a scoped INDEX would
        // report every one of them as unresolved.
        Findings(LintChecks.UnresolvedLink, "Notes").ShouldNotContain(f =>
            f.Subject.Contains("Mailvec", StringComparison.Ordinal));
        // And a broken link outside the scope stays outside the report.
        Findings(LintChecks.UnresolvedLink, "Notes").ShouldNotContain(f => f.Subject == "No Such Note");
        Findings(LintChecks.UnresolvedLink).ShouldContain(f => f.Subject == "No Such Note");
    }

    [Fact]
    public void A_heading_containing_a_wikilink_is_unreachable_and_its_inbound_links_are_broken()
    {
        // Obsidian anchors the heading by its RAW text, brackets included
        // (measured 2026-08-30), and no link can address that anchor — it
        // would terminate at the inner ']]'. So both spellings below are
        // genuinely broken, and Helios has six such links onto one heading.
        Findings(LintChecks.BrokenAnchor, "Cases").Select(f => f.Subject).ShouldBe(
        [
            "Homelab#Remote access — Tailscale Remote Access",
            "Homelab#Remote access — Tailscale Remote Access",
            "Homelab#Pipe",
        ]);
    }

    [Fact]
    public void An_escaped_pipe_ends_the_anchor_wherever_it_appears()
    {
        // Measured 2026-08-30: escaped, unescaped, and escaped-inside-a-table
        // all display the alias, so the fragment always ends at the pipe. A
        // heading that genuinely contains one is unreachable, and saying so
        // is correct — Helios has exactly one such link.
        Findings(LintChecks.BrokenAnchor, "Cases")
            .ShouldContain(f => f.Subject == "Homelab#Pipe");
    }

    [Fact]
    public void An_exact_case_match_settles_what_case_insensitive_lookup_made_ambiguous()
    {
        // [[CLAUDE]] matches CLAUDE.md and Tech/Claude.md; only one is exact.
        Findings(LintChecks.AmbiguousLink, "Cases").ShouldBeEmpty();
    }

    [Fact]
    public void The_nearest_note_settles_a_shared_basename()
    {
        // Kitchen/Nearest.md -> [[Cabinets]] with Kitchen/Cabinets.md beside
        // it: Obsidian resolves to the nearest, so this is not arbitrary...
        Findings(LintChecks.AmbiguousLink, "Kitchen").ShouldBeEmpty();
        // ...while the same link from a note in NEITHER folder still is.
        Findings(LintChecks.AmbiguousLink, "Notes").ShouldHaveSingleItem().Subject.ShouldBe("Cabinets");
    }

    [Fact]
    public void A_path_relative_to_the_linking_notes_folder_resolves()
    {
        // Tech/Homelab/Relative Path.md -> [[Proxmox/Monthly Maintenance]].
        // Root-only matching calls this broken; Obsidian follows it.
        Findings(LintChecks.UnresolvedLink, "Tech/Homelab").ShouldBeEmpty();
    }

    [Fact]
    public void Two_findings_at_one_position_survive_a_page_boundary_between_them()
    {
        // (path, line, column) is not unique for lint: one wikilink can be
        // both an unescaped pipe in a table row and an unresolved target. A
        // cursor keyed on position alone says "everything at that position is
        // done" after emitting the FIRST of them, so the second vanishes —
        // silently, on a final page still claiming truncated=false. The check
        // name is the tiebreaker that makes the order total.
        var whole = _vault.Lint.Lint(new LintQuery { PathPrefix = "Collision" }).Items;
        whole.Select(f => f.Check).ShouldBe([LintChecks.TablePipe, LintChecks.UnresolvedLink], ignoreOrder: true);
        whole.Select(f => (f.Line, f.Column)).Distinct().Count().ShouldBe(1);

        var paged = new List<LintFinding>();
        string? cursor = null;
        do
        {
            var page = _vault.Lint.Lint(new LintQuery
            {
                PathPrefix = "Collision", MaxResults = 1, Cursor = cursor,
            });
            paged.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        paged.ShouldBe(whole);
    }

    [Fact]
    public void Findings_paginate_in_position_order_without_omission_or_repeat()
    {
        var all = _vault.Lint.Lint(new LintQuery()).Items.ToList();
        all.Count.ShouldBeGreaterThan(3);

        var paged = new List<LintFinding>();
        string? cursor = null;
        do
        {
            var page = _vault.Lint.Lint(new LintQuery { MaxResults = 2, Cursor = cursor });
            page.Items.Count.ShouldBeLessThanOrEqualTo(2);
            paged.AddRange(page.Items);
            cursor = page.NextCursor;
            // The whole graph is built before anything is emitted, so the
            // total is known on every page rather than only the first.
            page.TotalMatches.ShouldBe(all.Count);
        }
        while (cursor is not null);

        paged.ShouldBe(all);
    }

    [Fact]
    public void A_page_that_exactly_empties_the_findings_is_not_truncated()
    {
        // The over-claim this pins: deciding truncation against the TOTAL
        // rather than against what remains after the cursor hands out a
        // cursor for a page with nothing in it.
        var total = _vault.Lint.Lint(new LintQuery()).Items.Count;
        var exact = _vault.Lint.Lint(new LintQuery { MaxResults = total });
        exact.Items.Count.ShouldBe(total);
        exact.Truncated.ShouldBeFalse();
        exact.NextCursor.ShouldBeNull();
    }

    [Fact]
    public void A_cursor_from_a_different_query_is_refused()
    {
        var cursor = _vault.Lint.Lint(new LintQuery { MaxResults = 1 }).NextCursor.ShouldNotBeNull();
        Should.Throw<KnapperException>(() => _vault.Lint.Lint(new LintQuery
        {
            MaxResults = 1, Cursor = cursor, Checks = [LintChecks.TablePipe],
        })).Code.ShouldBe(VaultErrorCode.InvalidCursor);
    }

    [Fact]
    public void An_unknown_check_name_is_a_typed_refusal()
    {
        Should.Throw<KnapperException>(() =>
            _vault.Lint.Lint(new LintQuery { Checks = ["no_such_check"] }))
            .Code.ShouldBe(VaultErrorCode.InvalidArgument);
    }

    [Fact]
    public void A_clean_scope_reports_nothing_and_claims_exhaustiveness()
    {
        var result = _vault.Lint.Lint(new LintQuery { PathPrefix = "Kitchen" });
        result.Items.ShouldBeEmpty();
        result.Truncated.ShouldBeFalse();
        result.TotalMatches.ShouldBe(0);
        result.UnexaminedFiles.ShouldBe(["legacy.md"]); // whole-vault index, whole-vault honesty
    }
}
