using System.Net;
using System.Text.Json;
using Knapper.Core;

namespace Knapper.Mcp.Tests;

public class HealthAndGuardTests : IClassFixture<KnapperMcpFactory>
{
    private readonly KnapperMcpFactory _factory;

    public HealthAndGuardTests(KnapperMcpFactory factory) => _factory = factory;

    [Fact]
    public async Task Health_is_ok_and_detailed_for_loopback()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("status").GetString().ShouldBe("ok");
        body.GetProperty("vault").GetProperty("reachable").GetBoolean().ShouldBeTrue();
        body.GetProperty("ripgrep").GetProperty("available").GetBoolean().ShouldBeTrue();
        body.GetProperty("audit").GetProperty("writable").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Up_mirrors_health_status_and_discloses_booleans_only()
    {
        using var client = _factory.CreateClient();
        var up = await client.GetAsync("/up");
        var health = await client.GetAsync("/health");
        up.StatusCode.ShouldBe(health.StatusCode);

        var body = await up.Content.ReadAsStringAsync();
        var root = JsonDocument.Parse(body).RootElement;
        root.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ShouldBe(
            ["audit", "conflicts", "oversized", "ripgrep", "status", "sync", "vault", "version"]);
        // The disclosures /up exists to avoid: no filesystem paths, no
        // conflict filenames, no generation counter.
        body.ShouldNotContain(_factory.VaultDir);
        body.ShouldNotContain("audit.jsonl");
        body.ShouldNotContain("generation");
        body.ShouldNotContain("root");

        // "oversized" is a BOOLEAN here, never the count or the filenames —
        // a count is on the generation-counter side of the line /up draws,
        // and a filename is vault content. Both live on /health.
        root.GetProperty("oversized").EnumerateObject().Select(p => p.Name).ShouldBe(["ok"]);
        body.ShouldNotContain("limitBytes");
        body.ShouldNotContain("count");
    }

    /// <summary>
    /// /health, /up and initialize.serverInfo.version are three surfaces of one
    /// process and must report one string — that identity is what makes
    /// `knapper verify` able to say "the URL that answers /up is the process
    /// serving the vault", and what lets an operator compare the deployed
    /// version against `knapper version` on the box. Three independent reads of
    /// the assembly would drift apart without anything failing: each would keep
    /// returning something version-shaped.
    /// </summary>
    [Fact]
    public async Task Every_surface_reports_the_same_build()
    {
        using var client = _factory.CreateClient();
        var health = JsonDocument.Parse(await client.GetStringAsync("/health")).RootElement;
        var up = JsonDocument.Parse(await client.GetStringAsync("/up")).RootElement;

        health.GetProperty("version").GetString().ShouldBe(BuildInfo.Version);
        up.GetProperty("version").GetString().ShouldBe(BuildInfo.Version);

        // Over the wire, where the client actually reads it. This previously
        // came from Assembly.GetEntryAssembly(), which under `dotnet test` is
        // the test host — so the assertion would have been about the runner.
        await using var mcp = await McpSurfaceTests.ConnectAsync(_factory);
        mcp.ServerInfo.Version.ShouldBe(BuildInfo.Version);
    }

    [Fact]
    public void Tool_surface_table_is_in_lockstep_with_the_attributes()
    {
        foreach (var (name, type) in ToolSurface.All)
        {
            var attributes = type.GetMethods()
                .SelectMany(m => m.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false))
                .Cast<ModelContextProtocol.Server.McpServerToolAttribute>()
                .ToList();
            attributes.ShouldHaveSingleItem($"{type.Name} must declare exactly one tool");
            attributes[0].Name.ShouldBe(name, $"{type.Name}'s attribute name must match the ToolSurface key");
        }
    }

    [Fact]
    public void Tool_surface_table_is_in_lockstep_with_the_names_the_verifier_asserts()
    {
        // `knapper verify --url` checks a DEPLOYED server against
        // Knapper.Core's ToolNames.All. If that list drifted from this table
        // the live check would happily assert the wrong surface — and it is
        // the only check standing between a partially-registered server and
        // production.
        ToolSurface.All.Keys.ShouldBe(Knapper.Core.ToolNames.All, ignoreOrder: true);
    }

    [Theory]
    [InlineData("localhost", null, true)]
    [InlineData("127.0.0.1", null, true)]
    [InlineData("mcp.example.com", null, false)] // not configured in this test set
    [InlineData("evil.example", null, false)]
    [InlineData("localhost", "https://evil.example", false)] // cross-origin browser POST
    [InlineData("localhost", "http://localhost:3535", true)]
    [InlineData("localhost", "not a url", false)]
    public void Host_guard_pins_host_and_origin(string host, string? origin, bool allowed)
    {
        var set = HostGuard.BuildAllowedHosts([]);
        HostGuard.IsAllowed(host, origin, set).ShouldBe(allowed);
    }

    [Fact]
    public void Host_guard_admits_the_configured_public_hostname()
    {
        var set = HostGuard.BuildAllowedHosts(["mcp.example.com"]);
        HostGuard.IsAllowed("mcp.example.com", null, set).ShouldBeTrue();
        HostGuard.IsAllowed("mcp.example.com", "https://mcp.example.com", set).ShouldBeTrue();
        HostGuard.IsAllowed("other.example.com", null, set).ShouldBeFalse();
    }

    [Fact]
    public async Task Hostile_host_header_is_rejected_before_any_route()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/up");
        request.Headers.Host = "evil.example";
        (await client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Audit_log_records_wire_mutations_with_caller_identity()
    {
        using var isolated = new KnapperMcpFactory(null);
        await using var client = await McpSurfaceTests.ConnectAsync(isolated);
        var sha = (await McpSurfaceTests.CallOk(client, "vault_read", new() { ["path"] = "Notes/Daily.md" }))
            .GetProperty("sha256").GetString();
        await McpSurfaceTests.CallOk(client, "vault_edit", new()
        {
            ["path"] = "Notes/Daily.md",
            ["expectSha256"] = sha,
            ["edits"] = new[] { new { old = "TODO alpha", @new = "DONE alpha" } },
        });

        var lines = File.ReadAllLines(Path.Combine(isolated.OutsideDir, "audit.jsonl"));
        var entry = lines.Select(l => JsonDocument.Parse(l).RootElement)
            .Single(e => e.GetProperty("Outcome").GetString() == "ok");
        entry.GetProperty("Op").GetString().ShouldBe("edit");
        entry.GetProperty("Client").GetString().ShouldBe("loopback"); // no Access assertion in tests
        entry.GetProperty("RequestId").GetString().ShouldNotBeNullOrEmpty();
    }
}
