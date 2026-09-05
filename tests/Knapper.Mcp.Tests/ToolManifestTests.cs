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

    /// <summary>
    /// Every string a client RENDERS is capped, not just the ones that are
    /// obviously prose. Claude Code delivers the first 2048 characters of a
    /// tool description and of the server instructions alike, silently and
    /// with no error on either side — measured 2026-09-05 against delivered
    /// copies of both, each ending mid-sentence at exactly index 2048.
    ///
    /// Nothing else here would ever notice. The server sends the full string,
    /// the manifest is well-formed, the tools answer calls correctly, health
    /// stays green, and every other test in this file reads the SERVER's
    /// copy — while the agent acts on text missing its tail. Two fields were
    /// over budget when this was written: the instructions had lost most of
    /// TRUST MODEL, and vault_lint's description ended mid-word at "moves
    /// bot", dropping the sentence that stops an agent reading a whole-vault
    /// run as a list of what recently broke.
    ///
    /// The same predicate runs in `knapper verify --url` against a DEPLOYED
    /// server, for the reason ToolNames and the schema contract are shared: a
    /// build gate and a deployment gate that disagree about what survives
    /// delivery are worse than one gate.
    /// </summary>
    [Fact]
    public async Task Every_string_a_client_renders_survives_delivery()
    {
        var tools = await ListToolsAsync();
        tools.Count.ShouldBe(ToolNames.All.Count); // a budget check over an empty list proves nothing

        var problems = tools
            .SelectMany(t => ToolSchemaContract.FindOverBudgetText(
                $"{t.GetProperty("name").GetString()} description",
                t.TryGetProperty("description", out var d) ? d.GetString() : null))
            .ToList();

        var discover = await (await RawMcp.OpenAsync(_factory.CreateClient())).DiscoverAsync();
        problems.AddRange(ToolSchemaContract.FindOverBudgetText(
            "server instructions",
            discover.TryGetProperty("instructions", out var i) ? i.GetString() : null));

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

    [Fact]
    public async Task No_tool_advertises_the_request_scoped_server_as_an_argument()
    {
        // Every tool method takes an McpServer parameter so the call log can
        // name the calling client application (ClientAppLoggingTests). The
        // SDK binds it from the request context and documents it as excluded
        // from the schema — but "documented as excluded" and "excluded" are
        // different claims, and if it ever leaked it would publish a required
        // argument no client can construct. That is the 0.3.2 shape: a
        // manifest defect takes the WHOLE tool list down, while every test
        // that merely CALLS a tool stays green.
        var advertised = (await ListToolsAsync())
            .Select(t => (Name: t.GetProperty("name").GetString()!, Schema: Schema(t, "inputSchema")))
            .Where(t => t.Schema is { } s
                && s.TryGetProperty("properties", out var properties)
                && properties.EnumerateObject().Any(p =>
                    p.Value.TryGetProperty("$ref", out _)
                    || p.Name.Contains("server", StringComparison.OrdinalIgnoreCase)))
            .Select(t => t.Name)
            .ToList();

        advertised.ShouldBeEmpty(
            $"the request-scoped server leaked into the input schema of: {string.Join(", ", advertised)}");
    }

    [Fact]
    public async Task Server_discover_carries_the_instructions_not_just_initialize()
    {
        // The CALL ECONOMICS paragraph is an intervention, and an
        // intervention that is never delivered cannot be measured. On CT 106
        // the claude.ai relay sent 613 server/discover and ONE initialize in
        // 14 days — dated 2026-08-17, a week BEFORE the release that shipped
        // that paragraph — so if discover ever stopped carrying instructions,
        // the majority surface would silently run on whatever it cached, and
        // every before/after window would measure a change that never
        // reached it. Nothing else here would notice: tools still list,
        // tools still call, health stays green.
        var discover = await (await RawMcp.OpenAsync(_factory.CreateClient())).DiscoverAsync();

        discover.TryGetProperty("instructions", out var instructions).ShouldBeTrue(
            "server/discover published no instructions — the relay's only channel for them");
        var text = instructions.GetString().ShouldNotBeNull();
        foreach (var landmark in new[] { "CALL ECONOMICS", "MUTATION PROTOCOL", "TRUST MODEL" })
            text.ShouldContain(landmark);
    }

    private async Task<IReadOnlyList<JsonElement>> ListToolsAsync() =>
        await (await RawMcp.OpenAsync(_factory.CreateClient())).ListToolsAsync();

    private static JsonElement? Schema(JsonElement tool, string which) =>
        tool.TryGetProperty(which, out var schema) ? schema : null;
}
