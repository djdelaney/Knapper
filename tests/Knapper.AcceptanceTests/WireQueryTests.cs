using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;

namespace Knapper.AcceptanceTests;

/// <summary>
/// Brief §13 read/query equivalence and budget semantics, black-box: the
/// server's answers are compared against a LOCAL ripgrep run by the test
/// over the same vault (same baseline args the server pins), pages are
/// recombined, a real timeout fires, a client cancel leaves the server
/// healthy, and a mutation mid-search proves changed_during_query.
/// </summary>
public sealed class WireQueryTests : IAsyncLifetime
{
    private readonly string _vaultDir = Wire.NewTempDir("knapper-accept-vault-");
    private readonly string _outsideDir = Wire.NewTempDir("knapper-accept-outside-");
    private AcceptanceServer _server = null!;
    private McpClient _client = null!;

    public async Task InitializeAsync()
    {
        Wire.Seed(_vaultDir, "Notes/Daily.md", "# Daily\nTODO alpha task\ntodo beta task\nDone gamma\nwrap TODO up\n");
        Wire.Seed(_vaultDir, "Notes/Sub/Deep.md", "deep content\nneedle here\n");
        Wire.Seed(_vaultDir, "with spaces/nöte – ünïcode.md", "Ünïcode käse\nnëëdlë ünïcode\n");
        Wire.Seed(_vaultDir, "empty.md", "");
        // 4 files x 15 matching lines = 60 'needle' matches → multi-page.
        for (var f = 0; f < 4; f++)
        {
            var sb = new StringBuilder();
            for (var l = 0; l < 15; l++)
                sb.Append($"line {l} needle {f}\n");
            Wire.Seed(_vaultDir, $"many/needles-{f}.md", sb.ToString());
        }
        // Binary: NUL bytes around a searchable word — excluded on BOTH sides.
        var blob = Path.Combine(_vaultDir, "raw/blob.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
        File.WriteAllBytes(blob, [0x00, 0x01, .. "needle"u8, 0x00, 0xFF]);
        // Bulk corpus so searches take real time (slow-search scenarios).
        for (var f = 0; f < 600; f++)
            Wire.Seed(_vaultDir, $"bulk/note-{f:D4}.md", $"bulk note {f}\nline with common text {f}\nmore filler\n");

        _server = new AcceptanceServer(_vaultDir, _outsideDir);
        _client = await _server.ConnectAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _client.DisposeAsync();
        _server.Dispose();
        Wire.TryDeleteDir(_vaultDir);
        Wire.TryDeleteDir(_outsideDir);
    }

    // ---- equivalence against a local rg over the same vault --------------

    [Theory]
    [InlineData("needle")]
    [InlineData(@"TODO \w+")]
    [InlineData("nëëdlë")]
    public async Task Search_matches_agree_with_local_ripgrep(string pattern)
    {
        var expected = LocalRgMatches(pattern);
        var actual = await AllMatchPositions(pattern);

        actual.ShouldBe(expected); // same records, same deterministic order
    }

    [Fact]
    public async Task File_listing_agrees_with_local_rg_files()
    {
        var expected = LocalRg("--files", "--sort=path")
            .Where(l => l.Length > 0)
            .Select(l => l.StartsWith("./", StringComparison.Ordinal) ? l[2..] : l)
            .ToList();

        var files = new List<string>();
        string? cursor = null;
        do
        {
            var page = await Wire.CallOk(_client, "vault_files", new() { ["cursor"] = cursor });
            files.AddRange(page.GetProperty("items").EnumerateArray()
                .Where(e => !e.GetProperty("isDirectory").GetBoolean())
                .Select(e => e.GetProperty("path").GetString()!));
            cursor = page.GetProperty("truncated").GetBoolean()
                ? page.GetProperty("nextCursor").GetString()
                : null;
        } while (cursor is not null);

        files.ShouldBe(expected);
    }

    [Fact]
    public async Task Sixty_matches_recombine_across_pages_and_match_rg_totals()
    {
        var expected = LocalRgMatches("needle", "many/");

        var all = new List<(string, int, int)>();
        string? cursor = null;
        var pages = 0;
        while (true)
        {
            var page = await Wire.CallOk(_client, "vault_search", new()
            {
                ["pattern"] = "needle",
                ["pathPrefixes"] = new[] { "many" },
                ["maxResults"] = 25,
                ["cursor"] = cursor,
            });
            all.AddRange(Positions(page));
            pages++;
            pages.ShouldBeLessThan(10);
            if (!page.GetProperty("truncated").GetBoolean())
                break;
            cursor = page.GetProperty("nextCursor").GetString().ShouldNotBeNull();
        }

        pages.ShouldBe(3); // 25 + 25 + 10
        all.ShouldBe(expected); // no duplicates, no omissions, stable order
        all.Count.ShouldBe(60);
    }

    [Fact]
    public async Task No_match_is_an_untruncated_empty_envelope_with_scan_evidence()
    {
        var result = await Wire.CallOk(_client, "vault_search", new() { ["pattern"] = "zzz_absent_zzz" });
        result.GetProperty("items").GetArrayLength().ShouldBe(0);
        result.GetProperty("truncated").GetBoolean().ShouldBeFalse();
        result.GetProperty("scannedFiles").GetInt32().ShouldBeGreaterThan(0);
    }

    // ---- timeout / cancel / changed-during-query -------------------------

    [Fact]
    public async Task A_time_budget_that_produced_nothing_is_a_typed_QueryTimeout()
    {
        // A dedicated 1 ms-budget server over a vault whose one file is big
        // enough that rg is still mid-scan with ZERO output when the kill
        // lands (a small corpus can finish inside the timer's real
        // granularity). No output + timeout must be the typed QueryTimeout —
        // never an empty "no match", which would claim exhaustive search.
        var vault = Wire.NewTempDir("knapper-accept-timeout-vault-");
        var outside = Wire.NewTempDir("knapper-accept-timeout-outside-");
        try
        {
            var line = new string('x', 4095) + "\n";
            File.WriteAllText(Path.Combine(vault, "huge.md"),
                string.Concat(Enumerable.Repeat(line, 16_384))); // 64 MiB, no match anywhere
            using var server = new AcceptanceServer(vault, outside,
                new Dictionary<string, string> { ["Vault__QueryTimeoutMs"] = "1" });
            await using var client = await server.ConnectAsync();

            var error = await Wire.CallError(client, "vault_search", new() { ["pattern"] = "zzz_absent_zzz" });
            error.ShouldContain("[QueryTimeout]");
        }
        finally
        {
            Wire.TryDeleteDir(vault);
            Wire.TryDeleteDir(outside);
        }
    }

    [Fact]
    public async Task A_client_cancel_mid_search_leaves_the_server_healthy()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(15));
        await Should.ThrowAsync<OperationCanceledException>(() =>
            _client.CallToolAsync("vault_search", new Dictionary<string, object?>
            {
                ["pattern"] = "common text",
                ["contextBefore"] = 3,
                ["contextAfter"] = 3,
            }, cancellationToken: cts.Token).AsTask());

        // The abandoned request must not wedge anything.
        (await _server.UpStatusAsync()).ShouldBe(System.Net.HttpStatusCode.OK);
        var result = await Wire.CallOk(_client, "vault_search", new() { ["pattern"] = "needle here" });
        result.GetProperty("items").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task A_mutation_landing_mid_search_flips_changed_during_query()
    {
        // The mutation goes THROUGH the server (its generation counter moves
        // synchronously at commit), so this doesn't depend on filesystem-
        // watcher latency — only on the create landing inside the search's
        // start→end window, hence the bounded retry.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var search = Wire.CallOk(_client, "vault_search", new()
            {
                ["pattern"] = "common text",
                ["contextBefore"] = 3,
                ["contextAfter"] = 3,
                ["maxResults"] = 200,
            });
            await Task.Delay(5);
            await Wire.CallOk(_client, "vault_create", new()
            {
                ["path"] = $"racer-{attempt}.md",
                ["text"] = "landed mid-search\n",
            });
            var result = await search;

            if (result.GetProperty("changedDuringQuery").GetBoolean())
            {
                result.GetProperty("generationEnd").GetInt64()
                    .ShouldBeGreaterThan(result.GetProperty("generationStart").GetInt64());
                return;
            }
        }
        Assert.Fail("a mutation never landed inside a search window in 20 attempts");
    }

    // ---- local rg oracle -------------------------------------------------

    /// <summary>One record per SUBMATCH, ordered by (path, line, column) — the server's contract.</summary>
    private List<(string Path, int Line, int Column)> LocalRgMatches(string pattern, string? scope = null)
    {
        var args = new List<string> { "--json", "--sort=path", "-e", pattern };
        // Name a target, exactly as the server does. Handed no path, rg may
        // search stdin instead of the directory — the oracle would then return
        // nothing and "disagree" with a server that was perfectly correct.
        args.Add(scope ?? ".");
        var records = new List<(string, int, int)>();
        foreach (var line in LocalRg([.. args]))
        {
            if (line.Length == 0)
                continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.GetProperty("type").GetString() != "match")
                continue;
            var data = root.GetProperty("data");
            var path = data.GetProperty("path").GetProperty("text").GetString()!;
            if (path.StartsWith("./", StringComparison.Ordinal))
                path = path[2..];
            var lineNumber = data.GetProperty("line_number").GetInt32();
            foreach (var sub in data.GetProperty("submatches").EnumerateArray())
                records.Add((path, lineNumber, sub.GetProperty("start").GetInt32() + 1));
        }
        return records;
    }

    /// <summary>The server's own baseline args, run from the vault root by the test.</summary>
    private List<string> LocalRg(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "rg",
            WorkingDirectory = _vaultDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var baseline in new[] { "--no-config", "--no-ignore", "--no-follow" })
            psi.ArgumentList.Add(baseline);
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var lines = new List<string>();
        while (process.StandardOutput.ReadLine() is { } line)
            lines.Add(line);
        process.WaitForExit(30_000).ShouldBeTrue("local rg never exited");
        return lines;
    }

    private async Task<List<(string, int, int)>> AllMatchPositions(string pattern)
    {
        var all = new List<(string, int, int)>();
        string? cursor = null;
        do
        {
            var page = await Wire.CallOk(_client, "vault_search", new()
            {
                ["pattern"] = pattern,
                ["cursor"] = cursor,
            });
            all.AddRange(Positions(page));
            cursor = page.GetProperty("truncated").GetBoolean()
                ? page.GetProperty("nextCursor").GetString()
                : null;
        } while (cursor is not null);
        return all;
    }

    private static IEnumerable<(string, int, int)> Positions(JsonElement page) =>
        page.GetProperty("items").EnumerateArray()
            .Select(m => (
                m.GetProperty("path").GetString()!,
                m.GetProperty("line").GetInt32(),
                m.GetProperty("column").GetInt32()))
            .ToList();
}
