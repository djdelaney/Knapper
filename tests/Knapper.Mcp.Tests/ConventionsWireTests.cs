using System.Text.Json;
using Knapper.Core;

namespace Knapper.Mcp.Tests;

/// <summary>
/// A deployment that CONFIGURES its conventions (Conventions:*), read off
/// the wire: what the write tools' descriptions state, and what a write that
/// breaks a checked rule gets back. The unconfigured case is
/// <see cref="ToolManifestTests.An_unconfigured_deployment_states_no_conventions"/>.
/// </summary>
public class ConventionsWireTests : IClassFixture<ConventionsWireTests.ConfiguredFactory>
{
    public sealed class ConfiguredFactory() : KnapperMcpFactory(new()
    {
        ["Conventions:WikilinksOnly"] = "true",
        ["Conventions:NoNewFrontmatter"] = "true",
        ["Conventions:NoNewTags"] = "true",
        ["Conventions:Style"] = "match the vault's terse, information-dense style",
        ["Conventions:NewNoteFolder"] = "Quicknotes",
    });

    private readonly ConfiguredFactory _factory;

    public ConventionsWireTests(ConfiguredFactory factory) => _factory = factory;

    /// <summary>
    /// A tool description is the only channel that reaches an agent at the
    /// moment it is DRAFTING, so a tool that WRITES states the conventions.
    /// The set is derived from readOnlyHint rather than listed: the failure
    /// this guards is a NEW write tool shipping without the clause, and a
    /// list would need the same edit that was already forgotten.
    /// vault_delete is the one exemption — it removes a note, so neither how
    /// one is written nor where one goes is in play.
    /// </summary>
    [Fact]
    public async Task Every_tool_that_writes_states_the_configured_conventions()
    {
        var tools = await (await RawMcp.OpenAsync(_factory.CreateClient())).ListToolsAsync();
        var writers = tools
            .Where(t => t.TryGetProperty("annotations", out var a)
                && a.TryGetProperty("readOnlyHint", out var ro)
                && ro.ValueKind == JsonValueKind.False)
            .ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("description").GetString() ?? "");

        writers.Keys.ShouldContain("vault_edit"); // a derived set that came back empty proves nothing

        foreach (var (name, description) in writers)
        {
            if (name == "vault_delete")
                continue;
            var states = description.Contains("[[wikilinks]]", StringComparison.Ordinal)
                || description.Contains("Quicknotes/", StringComparison.Ordinal);
            states.ShouldBeTrue(
                $"{name} writes to the vault but its description states no configured convention — add it to " +
                "VaultConventions.Clauses");
        }

        writers["vault_edit"].ShouldContain("do NOT add frontmatter to a note that lacks it, or tags");
        writers["vault_edit"].ShouldContain("terse, information-dense style");
        writers["vault_edit"].ShouldContain("warnings name any checked convention");
        writers["vault_create"].ShouldContain("New notes default to Quicknotes/");
        writers["vault_create"].ShouldContain("vault root comes back in the response's warnings");
        writers["vault_mkdir"].ShouldNotContain("CONVENTIONS:"); // chooses a path, writes no content
    }

    [Fact]
    public async Task Configured_descriptions_still_survive_delivery()
    {
        var tools = await (await RawMcp.OpenAsync(_factory.CreateClient())).ListToolsAsync();
        var problems = tools
            .SelectMany(t => ToolSchemaContract.FindOverBudgetText(
                $"{t.GetProperty("name").GetString()} description", t.GetProperty("description").GetString()))
            .ToList();
        problems.ShouldBeEmpty(string.Join("\n", problems));
    }

    [Fact]
    public async Task A_write_that_breaks_a_checked_convention_lands_and_says_so()
    {
        _factory.Seed("Notes/Plain.md", "# Plain\nNo frontmatter, no tags.\n");
        var session = await RawMcp.OpenAsync(_factory.CreateClient());
        var read = await session.CallToolAsync("vault_read", new { path = "Notes/Plain.md" });
        var sha = read.GetProperty("structuredContent").GetProperty("sha256").GetString()!;

        var result = await session.CallToolAsync("vault_append", new
        {
            path = "Notes/Plain.md",
            expectSha256 = sha,
            text = "See [the plan](Projects/plan.md) #todo\n",
        });

        if (result.TryGetProperty("isError", out var isError))
            isError.GetBoolean().ShouldBeFalse(result.ToString());
        var warnings = result.GetProperty("structuredContent").GetProperty("warnings")
            .EnumerateArray().Select(w => w.GetProperty("rule").GetString()).ToList();
        warnings.ShouldBe(["markdown_internal_link", "tags_added"], ignoreOrder: true);
        _factory.ReadVaultFile("Notes/Plain.md").ShouldContain("[the plan](Projects/plan.md)"); // advisory: it landed
    }

    [Fact]
    public async Task A_note_created_at_the_vault_root_is_flagged_and_one_in_a_folder_is_not()
    {
        var session = await RawMcp.OpenAsync(_factory.CreateClient());

        var stray = await session.CallToolAsync("vault_create", new { path = "Stray.md", text = "plain\n" });
        stray.GetProperty("structuredContent").GetProperty("warnings").EnumerateArray()
            .Select(x => x.GetProperty("rule").GetString()).ShouldBe(["note_at_vault_root"]);

        var filed = await session.CallToolAsync("vault_create", new { path = "Notes/Filed.md", text = "plain\n" });
        filed.GetProperty("structuredContent").GetProperty("warnings").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void A_malformed_convention_refuses_startup()
    {
        using var factory = new KnapperMcpFactory(new() { ["Conventions:NewNoteFolder"] = "../Elsewhere" });
        var ex = Should.Throw<Exception>(() => factory.CreateClient());
        ex.ToString().ShouldContain("Conventions");
    }
}
