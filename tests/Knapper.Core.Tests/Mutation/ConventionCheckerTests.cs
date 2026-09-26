using System.Text;
using Knapper.Core.Mutation;
using Knapper.Core.Options;

namespace Knapper.Core.Tests.Mutation;

/// <summary>
/// The write-side convention checks. Every rule is about what a write ADDED:
/// a note that already broke a convention before the agent touched it must
/// not warn on every later edit, or the warning becomes noise agents skip.
/// </summary>
public sealed class ConventionCheckerTests
{
    private static readonly ConventionChecker All = new(new ConventionsOptions
    {
        WikilinksOnly = true,
        NoNewFrontmatter = true,
        NoNewTags = true,
    });

    private static IReadOnlyList<string> Rules(string? before, string after, string path = "Notes/a.md", ConventionChecker? checker = null) =>
        [.. (checker ?? All).Check(path, before is null ? null : Encoding.UTF8.GetBytes(before), Encoding.UTF8.GetBytes(after))
            .Select(w => w.Rule)];

    [Fact]
    public void Nothing_is_checked_when_no_rule_is_enabled() =>
        Rules("x\n", "---\ntags: [a]\n---\n[b](c.md) #d\n", checker: ConventionChecker.Off).ShouldBeEmpty();

    [Theory]
    [InlineData("[plan](Projects/plan.md)")]
    [InlineData("![sketch](Attachments/shed.png)")]
    [InlineData("[plan](<Projects/my plan.md>)")]
    [InlineData("[section](#Budget)")]
    [InlineData("[plan](Projects/plan.md \"title\")")]
    public void An_added_markdown_link_to_vault_content_warns(string link) =>
        Rules("# Note\n", $"# Note\n{link}\n").ShouldBe(["markdown_internal_link"]);

    [Theory]
    [InlineData("[site](https://example.com/a)")]
    [InlineData("[mail](mailto:someone@example.com)")]
    [InlineData("[vault](obsidian://open?vault=x)")]
    [InlineData("[cdn](//cdn.example.com/x.js)")]
    [InlineData("[[Project Plan]]")]
    [InlineData("![[shed.png]]")]
    [InlineData("`[plan](Projects/plan.md)`")]
    [InlineData("\\[not](p.md)")]
    public void URLs_wikilinks_code_and_escapes_do_not_warn(string text) =>
        Rules("# Note\n", $"# Note\n{text}\n").ShouldBeEmpty();

    [Fact]
    public void A_link_inside_a_fenced_block_is_an_example_not_a_link() =>
        Rules("# Note\n", "# Note\n```md\n[plan](Projects/plan.md)\n```\n").ShouldBeEmpty();

    [Fact]
    public void A_markdown_link_that_was_already_there_does_not_warn_again() =>
        Rules("[plan](p.md)\n", "[plan](p.md)\nmore text\n").ShouldBeEmpty();

    [Fact]
    public void A_second_copy_of_an_existing_link_is_still_an_addition() =>
        Rules("[plan](p.md)\n", "[plan](p.md)\n[plan](p.md)\n").ShouldBe(["markdown_internal_link"]);

    [Fact]
    public void A_new_note_is_checked_for_links_but_not_for_frontmatter_or_tags() =>
        Rules(null, "---\ntype: recipe\ntags: [food]\n---\n[x](y.md) #dinner\n").ShouldBe(["markdown_internal_link"]);

    [Fact]
    public void Frontmatter_added_to_a_note_without_it_warns() =>
        Rules("# Note\nbody\n", "---\nstatus: active\n---\n# Note\nbody\n").ShouldBe(["frontmatter_added"]);

    [Fact]
    public void Editing_existing_frontmatter_does_not_warn() =>
        Rules("---\nstatus: a\n---\nbody\n", "---\nstatus: b\n---\nbody\n").ShouldBeEmpty();

    [Theory]
    [InlineData("#todo")]
    [InlineData("text #area/home")]
    public void A_first_inline_tag_on_a_tagless_note_warns(string line) =>
        Rules("# Note\nbody\n", $"# Note\nbody\n{line}\n").ShouldBe(["tags_added"]);

    [Fact]
    public void A_first_frontmatter_tag_on_an_existing_frontmatter_note_warns() =>
        Rules("---\nstatus: a\n---\nbody\n", "---\nstatus: a\ntags: [home]\n---\nbody\n").ShouldBe(["tags_added"]);

    [Fact]
    public void Adding_a_tag_to_a_note_that_already_uses_tags_does_not_warn() =>
        Rules("body #home\n", "body #home #garden\n").ShouldBeEmpty();

    [Theory]
    [InlineData("# Heading")]
    [InlineData("Issue #2026")]
    [InlineData("See [[Note#Heading]]")]
    [InlineData("https://example.com/#frag")]
    [InlineData("`#notatag`")]
    public void Headings_numbers_anchors_and_code_are_not_tags(string line) =>
        Rules("body\n", $"body\n{line}\n").ShouldBeEmpty();

    [Fact]
    public void Unparseable_frontmatter_before_the_write_means_could_not_tell_not_had_none() =>
        Rules("---\nstatus: [unclosed\n---\nbody\n", "---\nstatus: [unclosed\n---\nbody #tag\n").ShouldBeEmpty();

    [Fact]
    public void Only_markdown_notes_are_checked() =>
        Rules("x", "[plan](p.md) #tag", path: "scripts/notes.txt").ShouldBeEmpty();

    [Fact]
    public void Bytes_that_are_not_UTF8_yield_no_warning_rather_than_an_error() =>
        All.Check("Notes/a.md", [0x66, 0x6f], [0xff, 0xfe, 0x5b]).ShouldBeEmpty();

    /// <summary>
    /// Through the real mutation service: every receipt carries the list, an
    /// applied batch item carries its own, and a move (which adds no bytes)
    /// carries none — and the write lands either way.
    /// </summary>
    [Fact]
    public void Receipts_carry_warnings_and_the_writes_still_land()
    {
        using var v = new MutationVault();
        var service = v.ServiceWithConventions(new ConventionsOptions { WikilinksOnly = true, NoNewTags = true });
        var sha = v.Write("a.md", "# A\n");
        v.Write("b.md", "# B\n");

        var edit = service.Edit("a.md", sha, [new EditSpec("# A\n", "# A\n[x](b.md)\n")]);
        edit.Warnings.Select(w => w.Rule).ShouldBe(["markdown_internal_link"]);
        v.ReadText("a.md").ShouldContain("[x](b.md)");

        var batch = service.Batch([
            new BatchItem(BatchItemKind.Append, "a.md", edit.NewSha256, Text: "#first\n"),
            new BatchItem(BatchItemKind.Create, "c.md", Text: "plain\n"),
        ]);
        batch.Items[0].Warnings.Select(w => w.Rule).ShouldBe(["tags_added"]);
        batch.Items[1].Warnings.ShouldBeEmpty();

        var moved = service.Move("c.md", "d.md", batch.Items[1].NewSha256!);
        moved.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Only_the_enabled_rules_are_reported()
    {
        var linksOnly = new ConventionChecker(new ConventionsOptions { WikilinksOnly = true });
        Rules("body\n", "---\na: 1\n---\nbody [x](y.md) #t\n", checker: linksOnly).ShouldBe(["markdown_internal_link"]);
    }
}
