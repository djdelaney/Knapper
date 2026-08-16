using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;

namespace Knapper.Mcp.Tests;

/// <summary>
/// Wire-level tests: the real server driven through the SDK's own McpClient
/// over WebApplicationFactory's in-process handler — tools/list and
/// tools/call travel the same JSON-RPC path Claude uses. This file tests the
/// ENVELOPE (names, binding, error shape, registration); semantics live in
/// the Core tests.
/// </summary>
public class McpSurfaceTests : IClassFixture<KnapperMcpFactory>
{
    private readonly KnapperMcpFactory _factory;

    public McpSurfaceTests(KnapperMcpFactory factory) => _factory = factory;

    internal static Task<McpClient> ConnectAsync(WebApplicationFactory<Program> factory) =>
        ConnectAsync(factory, factory.CreateClient());

    /// <summary>Overload for callers that pre-configure the HttpClient (Host header, Access assertion).</summary>
    internal static async Task<McpClient> ConnectAsync(WebApplicationFactory<Program> factory, HttpClient http)
    {
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/") }, http);
        return await McpClient.CreateAsync(transport);
    }

    internal static async Task<JsonElement> CallOk(McpClient client, string tool, Dictionary<string, object?> args)
    {
        var result = await client.CallToolAsync(tool, args);
        (result.IsError ?? false).ShouldBeFalse(
            $"{tool} unexpectedly errored: {string.Join(" | ", result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text))}");
        result.StructuredContent.ShouldNotBeNull();
        return result.StructuredContent!.Value;
    }

    internal static async Task<string> CallError(McpClient client, string tool, Dictionary<string, object?> args)
    {
        var result = await client.CallToolAsync(tool, args);
        (result.IsError ?? false).ShouldBeTrue($"{tool} should have errored");
        return string.Join(" | ", result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text));
    }

    [Fact]
    public async Task Wire_exposes_exactly_the_locked_tool_surface()
    {
        await using var client = await ConnectAsync(_factory);
        var names = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
        names.ShouldBe(ToolSurface.All.Keys, ignoreOrder: true);
    }

    [Fact]
    public async Task Server_identifies_itself_and_carries_instructions()
    {
        await using var client = await ConnectAsync(_factory);
        client.ServerInfo.Name.ShouldBe("knapper");
        client.ServerInstructions.ShouldNotBeNull();
        client.ServerInstructions!.ShouldContain("single authoritative interface");
        client.ServerInstructions.ShouldContain("TRUST MODEL");
    }

    [Fact]
    public async Task Read_round_trip_returns_content_and_precondition_sha()
    {
        await using var client = await ConnectAsync(_factory);
        var read = await CallOk(client, "vault_read", new() { ["path"] = "Notes/Daily.md" });
        read.GetProperty("content").GetString().ShouldBe("# Daily\nTODO alpha\nDone beta\n");
        read.GetProperty("sha256").GetString()!.Length.ShouldBe(64);
        read.GetProperty("totalLines").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task Edit_round_trip_binds_structured_edit_ops_and_lands_on_disk()
    {
        await using var client = await ConnectAsync(_factory);
        _factory.Seed("edit-target.md", "before text\n");
        var sha = (await CallOk(client, "vault_read", new() { ["path"] = "edit-target.md" }))
            .GetProperty("sha256").GetString();

        var edit = await CallOk(client, "vault_edit", new()
        {
            ["path"] = "edit-target.md",
            ["expectSha256"] = sha,
            ["edits"] = new[] { new { old = "before text", @new = "after text", count = 1 } },
            ["guards"] = new[] { "text" },
        });

        edit.GetProperty("verified").GetBoolean().ShouldBeTrue();
        _factory.ReadVaultFile("edit-target.md").ShouldBe("after text\n");
    }

    [Fact]
    public async Task Search_round_trip_carries_the_completeness_envelope()
    {
        // ISOLATED factory, not the class fixture: the server runs a real
        // filesystem watcher over its vault, and sibling tests in this class
        // mutate the shared fixture — a delayed watcher event can then
        // legitimately advance the generation mid-search and flip
        // changedDuringQuery. This vault is seeded before the host (and its
        // watcher) starts, and nothing mutates it.
        using var isolated = new KnapperMcpFactory(null);
        await using var client = await ConnectAsync(isolated);
        var result = await CallOk(client, "vault_search", new() { ["pattern"] = "needle" });
        result.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        result.GetProperty("totalMatches").GetInt64().ShouldBe(2);
        result.GetProperty("items").EnumerateArray()
            .Select(m => m.GetProperty("path").GetString())
            .ShouldBe(["Notes/Sub/Deep.md", "Projects/plan.md"]);
        result.GetProperty("changedDuringQuery").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Frontmatter_round_trip_answers_with_the_same_flat_envelope_as_every_other_query()
    {
        // The RESPONSE half of the flattening (the manifest half is in
        // ToolManifestTests): the same field names, in the same place, as
        // vault_files and vault_search — plus this surface's own
        // unparseableFiles, which is what makes its "no match" trustworthy.
        using var isolated = new KnapperMcpFactory(null);
        await using var client = await ConnectAsync(isolated);
        var result = await CallOk(client, "vault_search_frontmatter", new() { ["field"] = "status" });

        result.TryGetProperty("envelope", out _).ShouldBeFalse("the envelope is nested again");
        result.GetProperty("items").EnumerateArray()
            .Select(m => m.GetProperty("path").GetString())
            .ShouldBe(["Projects/plan.md"]);
        result.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        result.GetProperty("totalMatches").GetInt64().ShouldBe(1);
        result.GetProperty("changedDuringQuery").GetBoolean().ShouldBeFalse();
        result.GetProperty("unparseableFiles").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Typed_errors_reach_the_wire_with_their_code_leading()
    {
        await using var client = await ConnectAsync(_factory);

        (await CallError(client, "vault_read", new() { ["path"] = "ghost.md" }))
            .ShouldContain("[NotFound]");
        (await CallError(client, "vault_read", new() { ["path"] = "../escape.md" }))
            .ShouldContain("[InvalidPath]");
        (await CallError(client, "vault_edit", new()
        {
            ["path"] = "Notes/Daily.md",
            ["expectSha256"] = new string('0', 64),
            ["edits"] = new[] { new { old = "TODO", @new = "DONE" } },
        })).ShouldContain("[PreconditionFailed]");
    }

    [Fact]
    public async Task Disabled_tools_vanish_from_list_and_call()
    {
        using var readOnly = new KnapperMcpFactory(new()
        {
            ["Mcp:DisabledTools:0"] = "vault_edit",
            ["Mcp:DisabledTools:1"] = "vault_delete",
        });
        await using var client = await ConnectAsync(readOnly);

        var names = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
        names.ShouldNotContain("vault_edit");
        names.ShouldNotContain("vault_delete");
        names.ShouldContain("vault_read");

        // tools/call on a disabled tool: the SDK surfaces the server's
        // "unknown tool" protocol error as a thrown exception.
        var ex = await Should.ThrowAsync<ModelContextProtocol.McpException>(async () =>
            await client.CallToolAsync("vault_edit", new Dictionary<string, object?>
            {
                ["path"] = "x.md",
                ["expectSha256"] = new string('0', 64),
                ["edits"] = Array.Empty<object>(),
            }));
        ex.Message.ShouldContain("vault_edit");
    }
}
