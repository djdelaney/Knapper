using System.Text;

namespace Knapper.AcceptanceTests;

/// <summary>
/// Brief §13 mutation safety "through the actual MCP transport": TWO real
/// server processes over one vault and one lock directory, raced by real
/// HTTP MCP clients. This is what the in-process suite cannot prove — flock
/// is per open-file-description, and the transport adds serialization and
/// timing the Core-level races never traverse.
/// </summary>
public sealed class TransportRaceTests : IAsyncLifetime
{
    private readonly string _vaultDir = Wire.NewTempDir("knapper-accept-vault-");
    private readonly string _outsideDir = Wire.NewTempDir("knapper-accept-outside-");
    private AcceptanceServer _serverA = null!;
    private AcceptanceServer _serverB = null!;
    private ModelContextProtocol.Client.McpClient _clientA = null!;
    private ModelContextProtocol.Client.McpClient _clientB = null!;

    public async Task InitializeAsync()
    {
        Wire.Seed(_vaultDir, "Notes/seed.md", "seed\n");
        _serverA = new AcceptanceServer(_vaultDir, _outsideDir);
        _serverB = new AcceptanceServer(_vaultDir, _outsideDir);
        _clientA = await _serverA.ConnectAsync();
        _clientB = await _serverB.ConnectAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _clientA.DisposeAsync();
        await _clientB.DisposeAsync();
        _serverA.Dispose();
        _serverB.Dispose();
        Wire.TryDeleteDir(_vaultDir);
        Wire.TryDeleteDir(_outsideDir);
    }

    [Fact]
    public async Task Concurrent_same_base_edits_across_processes_produce_exactly_one_winner()
    {
        Wire.Seed(_vaultDir, "counter.md", "value: 0\n");
        var sha = await Wire.ReadSha(_clientA, "counter.md");

        // Four writers, two per server process, all from the same base.
        var attempts = await Task.WhenAll(Enumerable.Range(1, 4).Select(i =>
            Wire.Call(i % 2 == 0 ? _clientA : _clientB, "vault_edit", new()
            {
                ["path"] = "counter.md",
                ["expectSha256"] = sha,
                ["edits"] = new[] { new { old = "value: 0", @new = $"value: {i}" } },
            })));

        attempts.Count(a => a.Ok).ShouldBe(1);
        foreach (var loser in attempts.Where(a => !a.Ok))
            loser.Error.ShouldContain("[PreconditionFailed]");

        // One winner's value, never a mangled mix — and the winner's receipt
        // hash matches the bytes actually on disk.
        var content = File.ReadAllText(Path.Combine(_vaultDir, "counter.md"));
        content.ShouldMatch(@"^value: [1-4]\n$");
        var winner = attempts.Single(a => a.Ok);
        winner.Payload!.Value.GetProperty("newSha256").GetString()
            .ShouldBe(Sha256(content));
    }

    [Fact]
    public async Task Simultaneous_no_clobber_creates_across_processes_yield_exactly_one_file()
    {
        var attempts = await Task.WhenAll(
            Wire.Call(_clientA, "vault_create", new() { ["path"] = "fresh.md", ["text"] = "from A\n" }),
            Wire.Call(_clientB, "vault_create", new() { ["path"] = "fresh.md", ["text"] = "from B\n" }));

        attempts.Count(a => a.Ok).ShouldBe(1);
        attempts.Single(a => !a.Ok).Error.ShouldContain("[AlreadyExists]");

        var content = File.ReadAllText(Path.Combine(_vaultDir, "fresh.md"));
        content.ShouldBeOneOf("from A\n", "from B\n");
        attempts.Single(a => a.Ok).Payload!.Value.GetProperty("newSha256").GetString()
            .ShouldBe(Sha256(content));
    }

    [Fact]
    public async Task An_edit_landed_through_one_process_stales_the_other_processes_base()
    {
        Wire.Seed(_vaultDir, "shared.md", "line one\n");
        var sha = await Wire.ReadSha(_clientA, "shared.md");

        await Wire.CallOk(_clientA, "vault_edit", new()
        {
            ["path"] = "shared.md",
            ["expectSha256"] = sha,
            ["edits"] = new[] { new { old = "line one", @new = "A's line" } },
        });

        // B retries with the now-stale base: clean typed rejection carrying
        // the re-read guidance, and A's write is untouched.
        var error = await Wire.CallError(_clientB, "vault_edit", new()
        {
            ["path"] = "shared.md",
            ["expectSha256"] = sha,
            ["edits"] = new[] { new { old = "line one", @new = "B's line" } },
        });
        error.ShouldContain("[PreconditionFailed]");
        error.ShouldContain("re-read");
        File.ReadAllText(Path.Combine(_vaultDir, "shared.md")).ShouldBe("A's line\n");
    }

    [Fact]
    public async Task Sequential_appends_from_fresh_reads_interleave_across_processes_without_loss()
    {
        Wire.Seed(_vaultDir, "log.md", "start\n");

        // The protocol agents must follow — fresh read before every write —
        // alternating processes each round. Nothing may be lost.
        var expected = new StringBuilder("start\n");
        for (var i = 0; i < 4; i++)
        {
            var client = i % 2 == 0 ? _clientA : _clientB;
            var sha = await Wire.ReadSha(client, "log.md");
            await Wire.CallOk(client, "vault_append", new()
            {
                ["path"] = "log.md",
                ["expectSha256"] = sha,
                ["text"] = $"entry {i}\n",
            });
            expected.Append($"entry {i}\n");
        }

        File.ReadAllText(Path.Combine(_vaultDir, "log.md")).ShouldBe(expected.ToString());
    }

    private static string Sha256(string content) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
