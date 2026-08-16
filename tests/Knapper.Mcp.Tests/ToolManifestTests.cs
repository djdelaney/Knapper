using System.Text.Json;
using Knapper.Core;

namespace Knapper.Mcp.Tests;

/// <summary>
/// The published MANIFEST — what a client reads before it can call anything.
/// tools/list is the one response where a single defect takes every tool down
/// at once: a client that cannot validate the list discards the WHOLE thing,
/// so twelve well-formed tools go dark over the thirteenth. That is how 0.3.2
/// shipped a vault_search no Claude Code client could load, with every wire
/// test green — calling a tool works fine through a useless schema, and
/// nothing here read the schemas.
///
/// Everything in this file reads the RAW wire response, never the SDK
/// client's view of it (see <see cref="RawMcp"/>).
/// </summary>
public class ToolManifestTests : IClassFixture<KnapperMcpFactory>
{
    private readonly KnapperMcpFactory _factory;

    public ToolManifestTests(KnapperMcpFactory factory) => _factory = factory;

    [Fact]
    public async Task Every_published_schema_is_a_schema_object_a_client_can_load()
    {
        var tools = await ListToolsAsync();
        tools.Count.ShouldBe(ToolNames.All.Count); // a manifest check over an empty list proves nothing

        // The SAME predicate `knapper verify --url` runs against a deployed
        // server — one definition, so the build gate and the deployment gate
        // cannot disagree about what a loadable manifest is.
        var problems = tools
            .SelectMany(t => ToolSchemaContract.Validate(
                t.GetProperty("name").GetString()!,
                Schema(t, "inputSchema"),
                Schema(t, "outputSchema")))
            .ToList();

        problems.ShouldBeEmpty(string.Join("\n", problems));
    }

    [Fact]
    public async Task Every_tool_publishes_an_output_schema_that_describes_its_result()
    {
        // Absent is VALID per the spec and passes the contract check above —
        // so on its own that check could be satisfied by dropping structured
        // output entirely, which is the opposite of the fix. Every tool here
        // returns structured content, so every tool must describe it.
        var undescribed = (await ListToolsAsync())
            .Where(t => Schema(t, "outputSchema") is null)
            .Select(t => t.GetProperty("name").GetString())
            .ToList();

        undescribed.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_search_schema_describes_the_envelope_and_every_item_field()
    {
        // vault_search is the tool that broke, and the only one whose result
        // is a union of three shapes — the schema has to survive that union
        // being expressed. Pinned field by field, not "is an object": a return
        // type loosened back to 'object' would satisfy a shape-only assertion
        // through the permissive schema that caused the outage.
        var search = (await ListToolsAsync()).Single(t => t.GetProperty("name").GetString() == "vault_search");
        var properties = Schema(search, "outputSchema").ShouldNotBeNull().GetProperty("properties");

        foreach (var field in new[]
                 {
                     "items", "truncated", "nextCursor", "scannedFiles", "returnedItems",
                     "totalMatches", "generationStart", "generationEnd", "changedDuringQuery",
                 })
        {
            properties.TryGetProperty(field, out var declared).ShouldBeTrue($"envelope field '{field}' undescribed");
            declared.ValueKind.ShouldBe(JsonValueKind.Object);
        }

        var item = properties.GetProperty("items").GetProperty("items");
        item.GetProperty("type").GetString().ShouldBe("object");
        var itemFields = item.GetProperty("properties");
        foreach (var field in new[] { "path", "line", "column", "text", "contextBefore", "contextAfter", "count" })
            itemFields.TryGetProperty(field, out _).ShouldBeTrue($"item field '{field}' undescribed");
    }

    [Fact]
    public async Task Every_query_surface_wears_the_envelope_at_the_top_level()
    {
        // One envelope, one place to find it. vault_search_frontmatter nested
        // its envelope under an `envelope` key until 0.5.0 — every field
        // present and correct, one level too deep — which forced any
        // client-side result parser to special-case one tool out of thirteen.
        // Nothing failed; it was simply a contract with a hole in it.
        var tools = (await ListToolsAsync()).ToDictionary(t => t.GetProperty("name").GetString()!);

        foreach (var name in new[] { "vault_files", "vault_search", "vault_search_frontmatter" })
        {
            var properties = Schema(tools[name], "outputSchema").ShouldNotBeNull().GetProperty("properties");
            foreach (var field in new[]
                     {
                         "items", "truncated", "nextCursor", "scannedFiles", "returnedItems",
                         "totalMatches", "generationStart", "generationEnd", "changedDuringQuery",
                     })
            {
                properties.TryGetProperty(field, out _)
                    .ShouldBeTrue($"{name}: envelope field '{field}' is not at the top level");
            }
            properties.TryGetProperty("envelope", out _)
                .ShouldBeFalse($"{name}: the envelope is nested under an 'envelope' key");
        }

        // The frontmatter surface's own addition survives the flattening: a
        // skipped file could be hiding a match, so "no match" is exhaustive
        // only once this list is empty.
        Schema(tools["vault_search_frontmatter"], "outputSchema").ShouldNotBeNull()
            .GetProperty("properties").TryGetProperty("unparseableFiles", out _).ShouldBeTrue();
    }

    private async Task<IReadOnlyList<JsonElement>> ListToolsAsync() =>
        await (await RawMcp.OpenAsync(_factory.CreateClient())).ListToolsAsync();

    private static JsonElement? Schema(JsonElement tool, string which) =>
        tool.TryGetProperty(which, out var schema) ? schema : null;
}
