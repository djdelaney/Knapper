using System.Net;
using System.Text.Json;

namespace Knapper.Mcp.Tests;

/// <summary>
/// The backstop for files the mutation guard cannot see: ones that arrived
/// from Dan's Macs or the Obsidian app already over Sync's ceiling. Obsidian
/// Sync refuses them and says nothing useful — it logs the rejection and
/// prints "Fully synced" in the same millisecond (CT 106, 2026-08-13) — so
/// without this the file is stranded with every signal green.
///
/// Its own fixture: a shared factory would leak the oversized file into every
/// other health assertion.
/// </summary>
public sealed class OversizedBackstopTests
{
    private static KnapperMcpFactory Factory() =>
        new(new Dictionary<string, string?> { ["Sync:MaxFileBytes"] = "1000" });

    [Fact]
    public async Task Health_names_the_oversized_files_and_the_limit()
    {
        using var factory = Factory();
        factory.Seed("Big/stranded.md", new string('x', 1500));
        using var client = factory.CreateClient();

        var body = JsonDocument.Parse(await client.GetStringAsync("/health")).RootElement;
        var oversized = body.GetProperty("oversized");

        oversized.GetProperty("count").GetInt32().ShouldBe(1);
        oversized.GetProperty("limitBytes").GetInt64().ShouldBe(1000);
        oversized.GetProperty("files").EnumerateArray().Select(f => f.GetString())
            .ShouldBe(["Big/stranded.md"]);
    }

    /// <summary>
    /// THE design decision, and the one most likely to be "fixed" later by
    /// someone reasoning that an unsyncable file is obviously unhealthy.
    ///
    /// It is not a 503, because a 503 is what the external monitor alerts on
    /// and what /up's parity with /health promises. A conflict file earns one:
    /// it BLOCKS mutations until a human reconciles it. An oversized file
    /// blocks nothing — the rest of the vault syncs normally — so a 503 here
    /// would be a permanent alert nobody can clear, which is precisely how the
    /// monitor's own cadence rules say an alert gets filtered into a folder
    /// nobody reads. It rides as a warning inside a 200 instead, and
    /// knapper-monitor.sh reads the field.
    /// </summary>
    [Fact]
    public async Task An_oversized_file_warns_without_degrading_status()
    {
        using var factory = Factory();
        factory.Seed("Big/stranded.md", new string('x', 1500));
        using var client = factory.CreateClient();

        var health = await client.GetAsync("/health");
        var up = await client.GetAsync("/up");

        health.StatusCode.ShouldBe(HttpStatusCode.OK);
        up.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonDocument.Parse(await health.Content.ReadAsStringAsync())
            .RootElement.GetProperty("status").GetString().ShouldBe("ok");

        // …while still being visible to anyone who reads the body.
        JsonDocument.Parse(await up.Content.ReadAsStringAsync())
            .RootElement.GetProperty("oversized").GetProperty("ok").GetBoolean().ShouldBeFalse();
    }

    /// <summary>
    /// /up may say THAT there is one, never WHICH — a filename is vault
    /// content, and /up is the surface reachable with the monitoring token.
    /// </summary>
    [Fact]
    public async Task Up_never_names_an_oversized_file()
    {
        using var factory = Factory();
        factory.Seed("Big/secret-project-name.md", new string('x', 1500));
        using var client = factory.CreateClient();

        var body = await client.GetStringAsync("/up");

        body.ShouldNotContain("secret-project-name");
        body.ShouldNotContain("Big/");
        body.ShouldNotContain("1500");
    }

    /// <summary>
    /// Dot-directories are skipped, matching what queries can see. .git
    /// packfiles and .obsidian plugin bundles routinely exceed the ceiling and
    /// none of them sync; reporting them would be permanent noise that trains
    /// the reader to ignore the warning entirely.
    /// </summary>
    [Fact]
    public async Task Files_under_dot_directories_are_not_reported()
    {
        using var factory = Factory();
        factory.Seed(".obsidian/plugins/omnisearch/main.js", new string('x', 5000));
        factory.Seed(".git/objects/pack/big.pack", new string('x', 5000));
        using var client = factory.CreateClient();

        var body = JsonDocument.Parse(await client.GetStringAsync("/health")).RootElement;

        body.GetProperty("oversized").GetProperty("count").GetInt32().ShouldBe(0);
        body.GetProperty("status").GetString().ShouldBe("ok");
    }

    [Fact]
    public async Task A_clean_vault_reports_none()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var body = JsonDocument.Parse(await client.GetStringAsync("/health")).RootElement;

        body.GetProperty("oversized").GetProperty("count").GetInt32().ShouldBe(0);
        JsonDocument.Parse(await client.GetStringAsync("/up")).RootElement
            .GetProperty("oversized").GetProperty("ok").GetBoolean().ShouldBeTrue();
    }
}
