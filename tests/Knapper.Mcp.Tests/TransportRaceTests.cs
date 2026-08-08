namespace Knapper.Mcp.Tests;

/// <summary>
/// The §13 mutation-safety races THROUGH the real MCP transport: two SDK
/// clients over the real JSON-RPC path (in-process handler; the on-box
/// acceptance run repeats these against the deployed service).
/// </summary>
public class TransportRaceTests : IClassFixture<KnapperMcpFactory>
{
    private readonly KnapperMcpFactory _factory;

    public TransportRaceTests(KnapperMcpFactory factory) => _factory = factory;

    [Fact]
    public async Task Stale_edit_through_the_transport_is_cleanly_rejected()
    {
        _factory.Seed("race/target.md", "base content\n");
        await using var clientA = await McpSurfaceTests.ConnectAsync(_factory);
        await using var clientB = await McpSurfaceTests.ConnectAsync(_factory);

        var sha = (await McpSurfaceTests.CallOk(clientA, "vault_read", new() { ["path"] = "race/target.md" }))
            .GetProperty("sha256").GetString();

        // A lands first.
        await McpSurfaceTests.CallOk(clientA, "vault_edit", new()
        {
            ["path"] = "race/target.md",
            ["expectSha256"] = sha,
            ["edits"] = new[] { new { old = "base content", @new = "A's content" } },
        });

        // B, holding the same (now stale) base: typed rejection, file intact.
        (await McpSurfaceTests.CallError(clientB, "vault_edit", new()
        {
            ["path"] = "race/target.md",
            ["expectSha256"] = sha,
            ["edits"] = new[] { new { old = "base content", @new = "B's content" } },
        })).ShouldContain("[PreconditionFailed]");

        _factory.ReadVaultFile("race/target.md").ShouldBe("A's content\n");
    }

    [Fact]
    public async Task Simultaneous_creates_through_the_transport_yield_exactly_one_file()
    {
        await using var clientA = await McpSurfaceTests.ConnectAsync(_factory);
        await using var clientB = await McpSurfaceTests.ConnectAsync(_factory);

        var a = clientA.CallToolAsync("vault_create", new Dictionary<string, object?>
        {
            ["path"] = "race/fresh.md",
            ["text"] = "from A\n",
        }).AsTask();
        var b = clientB.CallToolAsync("vault_create", new Dictionary<string, object?>
        {
            ["path"] = "race/fresh.md",
            ["text"] = "from B\n",
        }).AsTask();
        var results = await Task.WhenAll(a, b);

        // Parent dir "race" may not exist yet if this test runs first — then
        // both fail NotFound. Create it and retry once for determinism.
        if (results.All(r => r.IsError ?? false))
        {
            await McpSurfaceTests.CallOk(clientA, "vault_mkdir", new() { ["path"] = "race" });
            results =
            [
                await clientA.CallToolAsync("vault_create", new Dictionary<string, object?>
                    { ["path"] = "race/fresh.md", ["text"] = "from A\n" }),
                await clientB.CallToolAsync("vault_create", new Dictionary<string, object?>
                    { ["path"] = "race/fresh.md", ["text"] = "from B\n" }),
            ];
        }

        results.Count(r => !(r.IsError ?? false)).ShouldBe(1);
        _factory.ReadVaultFile("race/fresh.md").ShouldBeOneOf("from A\n", "from B\n");
    }

    [Fact]
    public async Task A_closed_sync_gate_fails_mutations_closed_but_reads_stay_up()
    {
        using var gated = new KnapperMcpFactory(new()
        {
            ["Sync:Mode"] = "heartbeat",
            ["Sync:HeartbeatPath"] = "/nonexistent/heartbeat",
        });
        await using var client = await McpSurfaceTests.ConnectAsync(gated);

        // Reads work.
        (await McpSurfaceTests.CallOk(client, "vault_read", new() { ["path"] = "Notes/Daily.md" }))
            .GetProperty("content").GetString()!.ShouldContain("Daily");

        // Mutations are hard typed failures — no fallback, nothing applied.
        var sha = (await McpSurfaceTests.CallOk(client, "vault_stat", new() { ["path"] = "Notes/Daily.md" }))
            .GetProperty("sha256").GetString();
        (await McpSurfaceTests.CallError(client, "vault_edit", new()
        {
            ["path"] = "Notes/Daily.md",
            ["expectSha256"] = sha,
            ["edits"] = new[] { new { old = "TODO", @new = "DONE" } },
        })).ShouldContain("[MutationBlocked]");
        gated.ReadVaultFile("Notes/Daily.md").ShouldContain("TODO");

        // And the monitor sees it: /up degrades.
        using var http = gated.CreateClient();
        (await http.GetAsync("/up")).StatusCode.ShouldBe(System.Net.HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task A_conflict_file_blocks_mutations_through_the_wire()
    {
        using var conflicted = new KnapperMcpFactory(null);
        conflicted.Seed("Notes/Plan.md", "original\n");
        conflicted.Seed("Notes/Plan (Conflicted copy 2026-08-08 120000).md", "conflicted\n");
        await using var client = await McpSurfaceTests.ConnectAsync(conflicted);

        var sha = (await McpSurfaceTests.CallOk(client, "vault_stat", new() { ["path"] = "Notes/Plan.md" }))
            .GetProperty("sha256").GetString();
        (await McpSurfaceTests.CallError(client, "vault_edit", new()
        {
            ["path"] = "Notes/Plan.md",
            ["expectSha256"] = sha,
            ["edits"] = new[] { new { old = "original", @new = "resolved" } },
        })).ShouldContain("[MutationBlocked]");

        // /health names the conflict for the human; /up only degrades.
        using var http = conflicted.CreateClient();
        var health = await (await http.GetAsync("/health")).Content.ReadAsStringAsync();
        health.ShouldContain("Conflicted copy");
        (await http.GetAsync("/up")).StatusCode.ShouldBe(System.Net.HttpStatusCode.ServiceUnavailable);
    }
}
