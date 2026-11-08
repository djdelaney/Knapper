using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;

namespace Knapper.AcceptanceTests;

/// <summary>
/// The client contract (transfer-grants §6): the grant's `fetch` text, run
/// LITERALLY by real bash with real curl and sha256sum, against a real server
/// process over a real socket. A recipe that only works with a newer curl,
/// a particular harness, or a step the text omits fails here — this test is
/// what keeps "portable" true rather than claimed.
///
/// The one test-boundary substitution: the server binds an ephemeral port it
/// cannot know before it starts, so the grant URL's configured placeholder
/// port is swapped for the bound one (§10 allows overriding the URL transport
/// at the test boundary). Everything else is the grant's own text.
/// </summary>
public sealed class DownloadRecipeTests : IAsyncLifetime
{
    private const string PlaceholderOrigin = "http://127.0.0.1:9";

    private readonly string _vaultDir = Wire.NewTempDir("knapper-dl-vault-");
    private readonly string _outsideDir = Wire.NewTempDir("knapper-dl-outside-");
    private readonly string _clientDir = Wire.NewTempDir("knapper-dl-client-");
    private AcceptanceServer _server = null!;
    private McpClient _client = null!;

    async Task IAsyncLifetime.InitializeAsync()
    {
        _server = new AcceptanceServer(_vaultDir, _outsideDir, new Dictionary<string, string>
        {
            ["Transfer__LocalOnly"] = "true",
            ["Transfer__PublicOrigin"] = PlaceholderOrigin,
            ["Download__Enabled"] = "true",
            ["Download__AllowedPrefixes__0"] = "Photos",
        });
        _client = await _server.ConnectAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _client.DisposeAsync();
        _server.Dispose();
        Wire.TryDeleteDir(_vaultDir);
        Wire.TryDeleteDir(_outsideDir);
        Wire.TryDeleteDir(_clientDir);
    }

    private void SeedBytes(string relative, byte[] bytes)
    {
        var path = Path.Combine(_vaultDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private async Task<JsonElement[]> PrepareAsync(params string[] paths)
    {
        var result = await Wire.CallOk(_client, "vault_download_prepare", new Dictionary<string, object?>
        {
            ["items"] = paths.Select(p => new Dictionary<string, object?> { ["path"] = p }).ToArray(),
        });
        return [.. result.GetProperty("items").EnumerateArray()];
    }

    private (int Exit, string Stderr) RunFetch(JsonElement grant, string dest, string? shaOverride = null)
    {
        var psi = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _clientDir,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(grant.GetProperty("fetch").GetString()!); // literally, prose comments and all
        psi.Environment["TOKEN"] = grant.GetProperty("downloadToken").GetString();
        psi.Environment["URL"] = grant.GetProperty("downloadUrl").GetString()!
            .Replace(PlaceholderOrigin, $"http://127.0.0.1:{_server.Port}", StringComparison.Ordinal);
        psi.Environment["SHA"] = shaOverride ?? grant.GetProperty("sha256").GetString();
        psi.Environment["LEN"] = grant.GetProperty("byteLength").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
        psi.Environment["DEST"] = dest;
        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stderr.Result);
    }

    private string[] StrayTemps() => Directory.GetFiles(_clientDir, ".knapper-dl.*");

    [Fact]
    public async Task The_fetch_recipe_runs_as_written_and_lands_exact_bytes()
    {
        var cases = new Dictionary<string, byte[]>
        {
            ["Photos/empty.bin"] = [],
            ["Photos/note.md"] = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("line\r\n", 3500)))],
            ["Photos/invalid-utf8.txt"] = [0x66, 0x6F, 0xFF, 0xFE, 0x80, 0x0A],
            ["Photos/photo.jpg"] = RandomNumberGenerator.GetBytes(2_900_000),
            ["Photos/at-ceiling.bin"] = RandomNumberGenerator.GetBytes(5 * 1024 * 1024),
        };
        foreach (var (path, bytes) in cases)
            SeedBytes(path, bytes);

        var items = await PrepareAsync([.. cases.Keys]);

        foreach (var item in items)
        {
            var path = item.GetProperty("path").GetString()!;
            var grant = item.GetProperty("grant");
            grant.ValueKind.ShouldBe(JsonValueKind.Object, $"{path}: {item}");
            grant.GetProperty("fetch").GetString()!.ShouldContain("--proto '=http'");

            var dest = Path.Combine(_clientDir, Path.GetFileName(path));
            var (exit, stderr) = RunFetch(grant, dest);

            exit.ShouldBe(0, $"{path}: {stderr}");
            File.ReadAllBytes(dest).ShouldBe(cases[path], $"{path} landed with different bytes");
        }
        StrayTemps().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_second_run_of_a_used_grant_fails_and_moves_nothing_into_place()
    {
        SeedBytes("Photos/once.bin", [1, 2, 3]);
        var grant = (await PrepareAsync("Photos/once.bin"))[0].GetProperty("grant");
        var dest = Path.Combine(_clientDir, "once.bin");

        RunFetch(grant, dest).Exit.ShouldBe(0);
        File.Delete(dest);

        var (exit, _) = RunFetch(grant, dest);
        exit.ShouldNotBe(0); // curl exit 22: the server refused (409)
        File.Exists(dest).ShouldBeFalse();
        StrayTemps().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_hash_mismatch_never_reaches_the_destination()
    {
        SeedBytes("Photos/checked.bin", [9, 9, 9]);
        var grant = (await PrepareAsync("Photos/checked.bin"))[0].GetProperty("grant");
        var dest = Path.Combine(_clientDir, "checked.bin");

        var (exit, _) = RunFetch(grant, dest, shaOverride: new string('0', 64));

        exit.ShouldNotBe(0);
        File.Exists(dest).ShouldBeFalse();
        StrayTemps().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refused_connection_claims_nothing_so_the_same_grant_still_works()
    {
        SeedBytes("Photos/retry.bin", [4, 5, 6]);
        var grant = (await PrepareAsync("Photos/retry.bin"))[0].GetProperty("grant");
        var dest = Path.Combine(_clientDir, "retry.bin");

        // Port 9 (discard) is closed: curl exits 7, never connected.
        var psi = new ProcessStartInfo("bash") { RedirectStandardError = true, WorkingDirectory = _clientDir };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(grant.GetProperty("fetch").GetString()!);
        psi.Environment["TOKEN"] = grant.GetProperty("downloadToken").GetString();
        psi.Environment["URL"] = grant.GetProperty("downloadUrl").GetString();
        psi.Environment["SHA"] = grant.GetProperty("sha256").GetString();
        psi.Environment["LEN"] = "3";
        psi.Environment["DEST"] = dest;
        using (var p = Process.Start(psi)!)
        {
            p.WaitForExit();
            p.ExitCode.ShouldNotBe(0);
        }
        File.Exists(dest).ShouldBeFalse();

        RunFetch(grant, dest).Exit.ShouldBe(0);
        File.ReadAllBytes(dest).ShouldBe(new byte[] { 4, 5, 6 });
    }
}
