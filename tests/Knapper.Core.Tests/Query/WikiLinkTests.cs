using Knapper.Core.Query;

namespace Knapper.Core.Tests.Query;

/// <summary>
/// The false-positive corpus, per proposal §8: "the expensive knowledge in
/// the prototype is not its structure … but the classes of thing that LOOK
/// like findings and are not", and they land as fixtures BEFORE the checks.
/// Every case here is either one of §8's named classes or something the
/// 2026-08-30 pass over Helios actually produced.
/// </summary>
public sealed class WikiLinkTests
{
    private static WikiLink.NoteShape Parse(params string[] lines) =>
        WikiLink.Parse(string.Join('\n', lines) + "\n");

    [Fact]
    public void Bash_test_syntax_in_a_fence_is_not_a_link()
    {
        // §8's first class: `[[ -t 1 ]]` reads as a wikilink to a naive scan,
        // and these notes are mostly shell.
        Parse("```sh", "if [[ -t 1 ]]; then echo tty; fi", "```").Links.ShouldBeEmpty();
    }

    [Fact]
    public void Bash_test_syntax_in_inline_code_is_not_a_link()
    {
        Parse("Guard it with `[[ -t 1 ]]` first.").Links.ShouldBeEmpty();
    }

    [Fact]
    public void A_backtick_run_only_closes_on_an_equal_run()
    {
        // ``a `b` c`` is one span: the single backtick inside does not close it.
        Parse("``code [[ -t 1 ]] more``").Links.ShouldBeEmpty();
        // An UNCLOSED run is literal text (CommonMark), so it must not mask
        // the rest of the line and swallow a real link.
        Parse("a ` stray backtick and [[Real Note]]").Links
            .ShouldHaveSingleItem().Target.ShouldBe("Real Note");
    }

    [Fact]
    public void A_tilde_fence_is_not_closed_by_a_backtick_fence()
    {
        Parse("~~~", "```", "[[Not A Link]]", "```", "~~~").Links.ShouldBeEmpty();
    }

    [Fact]
    public void An_escaped_pipe_splits_the_alias_and_leaves_no_trailing_backslash()
    {
        // §8's second class: a naive split yields a target of 'Note\', which
        // resolves to nothing — ~85 of the prototype's first 93 findings were
        // junk of this kind.
        var link = Parse("| a | [[Note\\|Alias]] |", "|---|---|", "| x | y |").Links.ShouldHaveSingleItem();
        link.Target.ShouldBe("Note");
        link.Alias.ShouldBe("Alias");
        link.HasUnescapedPipe.ShouldBeFalse();
    }

    [Fact]
    public void An_unescaped_pipe_inside_a_table_row_is_flagged_but_still_parses()
    {
        // The measured 22: the link is fine, the TABLE is what breaks.
        var link = Parse("| a | b |", "|---|---|", "| [[Note|Alias]] | y |").Links.ShouldHaveSingleItem();
        link.Target.ShouldBe("Note");
        link.Alias.ShouldBe("Alias");
        link.HasUnescapedPipe.ShouldBeTrue();
        link.InTableRow.ShouldBeTrue();
    }

    [Fact]
    public void An_escaped_pipe_separates_the_alias_wherever_it_appears()
    {
        // Measured in Obsidian 2026-08-30: the escaped and unescaped forms
        // and the escaped form inside a table row ALL display the alias, so
        // there is one rule and no table context. See Split's remarks.
        var link = Parse("See [[Homelab#every `curl \\| sh` installer fails]].").Links.ShouldHaveSingleItem();
        link.Target.ShouldBe("Homelab");
        link.Fragment.ShouldBe("every `curl"); // trimmed at the separator
        link.Alias.ShouldBe(" sh` installer fails");
    }

    [Fact]
    public void Emphasis_and_code_markup_stay_part_of_a_heading_anchor()
    {
        // Obsidian's heading suggester offers these verbatim (measured
        // 2026-08-30), so stripping the markup would accept links Obsidian
        // does not resolve. Only LINK syntax is stripped, and only because
        // '[[' cannot survive inside a '[[…#…]]' link.
        Parse("## Target **bold** heading").Headings.ShouldBe(["target **bold** heading"]);
        Parse("## Target `curl` heading").Headings.ShouldBe(["target `curl` heading"]);
    }

    [Fact]
    public void A_heading_containing_a_wikilink_keeps_its_raw_link_syntax()
    {
        // Measured 2026-08-30: the heading suggester offers
        // 'Target link — [[Some Missing Note]]' verbatim. Such a heading is
        // therefore UNREACHABLE — a link addressing it would terminate at the
        // inner ']]' — so every inbound link to it is genuinely broken and
        // reporting them is correct.
        Parse("## Remote access — [[Tailscale Remote Access]]").Headings
            .ShouldBe(["remote access — [[tailscale remote access]]"]);
        Parse("## A [markdown](https://example.com) link").Headings
            .ShouldBe(["a [markdown](https://example.com) link"]);
    }

    [Fact]
    public void Setext_headings_are_indexed()
    {
        // Obsidian anchors them (measured: the suggester lists the setext
        // target as H1). Skipping them reports every link to one as broken.
        Parse("Underlined title", "================", "body").Headings
            .ShouldBe(["underlined title"]);
        Parse("Dash form", "---", "body").Headings.ShouldBe(["dash form"]);
        // A rule with no paragraph above it heads nothing.
        Parse("", "---", "body").Headings.ShouldBeEmpty();
        // And a fence must not let its last line become a heading.
        Parse("```", "code", "```", "---").Headings.ShouldBeEmpty();
    }

    [Fact]
    public void A_pipe_outside_a_table_is_an_ordinary_alias()
    {
        var link = Parse("See [[Note|Alias]] for details.").Links.ShouldHaveSingleItem();
        link.HasUnescapedPipe.ShouldBeTrue();
        link.InTableRow.ShouldBeFalse(); // no delimiter row: a shell pipeline is not a table
    }

    [Fact]
    public void A_shell_pipeline_is_not_a_table_row()
    {
        Parse("Run ps aux | grep knapper and read [[Notes/Ops]].").Links
            .ShouldHaveSingleItem().InTableRow.ShouldBeFalse();
    }

    [Fact]
    public void Embeds_are_marked_and_never_confused_with_links()
    {
        // §8: attachments are not notes and must not be reported as missing ones.
        var links = Parse("![[diagram.png]] and [[Real Note]]").Links;
        links.Count.ShouldBe(2);
        links[0].IsEmbed.ShouldBeTrue();
        links[0].Target.ShouldBe("diagram.png");
        links[1].IsEmbed.ShouldBeFalse();
    }

    [Fact]
    public void Fragments_split_into_headings_and_block_ids()
    {
        var heading = Parse("[[Mailvec Stack#Step 8 — Backups (VM)]]").Links.ShouldHaveSingleItem();
        heading.Target.ShouldBe("Mailvec Stack");
        heading.Fragment.ShouldBe("Step 8 — Backups (VM)");
        heading.FragmentIsBlockId.ShouldBeFalse();

        var block = Parse("[[Note#^abc-123]]").Links.ShouldHaveSingleItem();
        block.Fragment.ShouldBe("abc-123");
        block.FragmentIsBlockId.ShouldBeTrue();

        var sameFile = Parse("[[#Local Heading]]").Links.ShouldHaveSingleItem();
        sameFile.Target.ShouldBe("");
        sameFile.Fragment.ShouldBe("Local Heading");

        // Nested #A#B: the deepest segment is what gets checked.
        Parse("[[Note#Outer#Inner]]").Links.ShouldHaveSingleItem().Fragment.ShouldBe("Inner");
    }

    [Fact]
    public void A_heading_needs_a_space_after_its_hashes()
    {
        // '##Foo' renders as literal text in Obsidian, so indexing it would
        // create a heading no link can reach.
        Parse("## Real Heading", "##NotAHeading").Headings.ShouldBe(["real heading"]);
    }

    [Fact]
    public void Comments_inside_a_fence_are_not_headings()
    {
        // The single most dangerous heading false positive in this vault:
        // every shell block is full of '# comment' lines.
        Parse("```sh", "# install the thing", "apt install rg", "```", "## Real")
            .Headings.ShouldBe(["real"]);
    }

    [Fact]
    public void Frontmatter_is_not_scanned_for_headings_but_is_scanned_for_links()
    {
        var shape = Parse("---", "title: '# not a heading'", "up: '[[Parent Note]]'", "---", "# Real");
        shape.Headings.ShouldBe(["real"]);
        shape.Links.ShouldHaveSingleItem().Target.ShouldBe("Parent Note");
    }

    [Fact]
    public void Heading_comparison_is_case_and_whitespace_insensitive_but_keeps_punctuation()
    {
        WikiLink.NormalizeHeading("  Step 8 —   Backups (VM) ").ShouldBe("step 8 — backups (vm)");
        WikiLink.NormalizeHeading("Measured throughput — 2026-08-11")
            .ShouldNotBe(WikiLink.NormalizeHeading(
                "Measured throughput — 2026-08-11 (historical — single Crucial P3 Plus)"));
    }

    [Fact]
    public void Block_ids_are_read_from_the_end_of_a_line()
    {
        Parse("Some claim worth citing. ^claim-1", "x^2 is not a block id", "^leading-is-fine")
            .BlockIds.ShouldBe(["claim-1", "leading-is-fine"]);
    }

    [Fact]
    public void Columns_are_utf8_byte_offsets_like_every_other_position_this_server_reports()
    {
        // 'Ünïcode ' is 8 chars but 10 UTF-8 bytes; a char offset here would
        // disagree with vault_search's columns over the same line.
        var link = Parse("Ünïcode [[Note]]").Links.ShouldHaveSingleItem();
        link.Column.ShouldBe(11);
        link.Line.ShouldBe(1);
    }

    [Fact]
    public void An_unterminated_link_ends_the_scan_of_its_line()
    {
        Parse("[[unterminated and [[Also Not Closed").Links.ShouldBeEmpty();
        Parse("[[Good]] then [[unterminated").Links
            .ShouldHaveSingleItem().Target.ShouldBe("Good");
    }
}
