using System.Net;
using System.Text.Json;

namespace Knapper.Mcp.Tests;

/// <summary>
/// The backstop for oversized files the mutation guard did not create: one
/// written by a human shell on the CT, or predating the guard. Obsidian Sync
/// refuses them and says nothing useful — it logs the rejection and prints
/// "Fully synced" in the same millisecond (CT 106, 2026-08-13) — so without
/// this the file is stranded with every signal green.
///
/// ⚠️ NOT a backstop for oversized files made on Dan's Macs. Measured
/// 2026-08-13: the ceiling is symmetric, so such a file never reaches CT 106
/// and there is nothing local to find. That gap is real, strictly worse, and
/// open — see docs/extending.md. Do not read these tests as covering it.
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
        body.GetProperty("oversized").GetProperty("scanned").GetBoolean().ShouldBeTrue();
        JsonDocument.Parse(await client.GetStringAsync("/up")).RootElement
            .GetProperty("oversized").GetProperty("ok").GetBoolean().ShouldBeTrue();
    }

    /// <summary>
    /// Acceptance for the walk that used to follow directory symlinks: a cycle
    /// inside the vault must not hang the endpoints. This walk runs on the
    /// /health and /up request path, and from runbook §8 the host monitor
    /// polls /up every 5 minutes — a hung walk is a hung health endpoint on a
    /// fixed cadence, blinding the very thing that would report it.
    ///
    /// A cycle would have to be made by hand on a Mac (Sync does not carry
    /// symlinks and Knapper refuses to create them), which is why it is a
    /// low-probability, high-blast-radius, cheap-to-close hole rather than an
    /// outage that already happened.
    /// </summary>
    [Fact]
    public async Task A_directory_symlink_cycle_does_not_hang_health_or_up()
    {
        using var factory = Factory();
        factory.Seed("Big/stranded.md", new string('x', 1500));
        Directory.CreateSymbolicLink(Path.Combine(factory.VaultDir, "Big", "loop"), factory.VaultDir);
        using var client = factory.CreateClient();

        // The endpoints answering at all is the assertion; the timeout keeps a
        // regression a failing test rather than a hung test run.
        var health = await client.GetAsync("/health").WaitAsync(TimeSpan.FromSeconds(30));
        var up = await client.GetAsync("/up").WaitAsync(TimeSpan.FromSeconds(30));

        health.StatusCode.ShouldBe(HttpStatusCode.OK);
        up.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = JsonDocument.Parse(await health.Content.ReadAsStringAsync()).RootElement;
        var oversized = body.GetProperty("oversized");
        oversized.GetProperty("scanned").GetBoolean().ShouldBeTrue();
        oversized.GetProperty("files").EnumerateArray().Select(f => f.GetString())
            .ShouldBe(["Big/stranded.md"]); // once, and not under the symlink
    }

    /// <summary>
    /// "Could not tell" must not render as "checked, all clear". The cache
    /// starts empty, so a walk that failed on the FIRST call returned that
    /// empty list and /up reported oversized.ok true — invisible by
    /// construction, because nobody investigates a clean report. The cold
    /// cache exists immediately after a start: exactly when the monitor polls
    /// and exactly when transient IO problems are likeliest.
    ///
    /// Unlike oversized files FOUND, this degrades. Nothing is blocked by a
    /// stranded file and a permanent 503 is an alert nobody can clear — but a
    /// scan that cannot complete is an error, it clears itself when the vault
    /// becomes readable, and until it does every word this probe says is
    /// unfounded. Non-root: mode 000 is what makes the walk throw.
    /// </summary>
    [Fact]
    public async Task A_scan_that_could_not_complete_is_not_reported_as_clean()
    {
        using var factory = Factory();
        var unreadable = Path.Combine(factory.VaultDir, "Locked");
        Directory.CreateDirectory(unreadable);
        File.SetUnixFileMode(unreadable, UnixFileMode.None);
        try
        {
            using var client = factory.CreateClient();

            var health = await client.GetAsync("/health");
            var up = await client.GetAsync("/up");

            // 503, not 500: an unreadable directory used to throw out of the
            // conflict walk and break /health's own status-code contract.
            health.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            up.StatusCode.ShouldBe(health.StatusCode);

            var body = JsonDocument.Parse(await health.Content.ReadAsStringAsync()).RootElement;
            body.GetProperty("oversized").GetProperty("scanned").GetBoolean().ShouldBeFalse();
            body.GetProperty("oversized").GetProperty("count").GetInt32().ShouldBe(0);
            body.GetProperty("vault").GetProperty("conflictScanComplete").GetBoolean().ShouldBeFalse();

            JsonDocument.Parse(await up.Content.ReadAsStringAsync()).RootElement
                .GetProperty("oversized").GetProperty("ok").GetBoolean().ShouldBeFalse();
        }
        finally
        {
            File.SetUnixFileMode(unreadable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
