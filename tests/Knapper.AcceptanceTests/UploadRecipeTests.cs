using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ModelContextProtocol.Client;

namespace Knapper.AcceptanceTests;

/// <summary>
/// The upload client contract (transfer-grants §6): the grant's `send` text,
/// run LITERALLY by real bash and curl against a real server process, then
/// committed over MCP. Bodies above 1 MiB make curl send
/// `Expect: 100-continue`, so the interim-response path is exercised too.
/// The one test-boundary substitution is the grant URL's placeholder port
/// (see <see cref="DownloadRecipeTests"/>).
/// </summary>
public sealed class UploadRecipeTests : IAsyncLifetime
{
    private const string PlaceholderOrigin = "http://127.0.0.1:9";

    private readonly string _vaultDir = Wire.NewTempDir("knapper-ul-vault-");
    private readonly string _outsideDir = Wire.NewTempDir("knapper-ul-outside-");
    private readonly string _clientDir = Wire.NewTempDir("knapper-ul-client-");
    private AcceptanceServer _server = null!;
    private McpClient _client = null!;

    async Task IAsyncLifetime.InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_vaultDir, "Uploads"));
        _server = new AcceptanceServer(_vaultDir, _outsideDir, new Dictionary<string, string>
        {
            ["Transfer__LocalOnly"] = "true",
            ["Transfer__PublicOrigin"] = PlaceholderOrigin,
            ["Upload__Enabled"] = "true",
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

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private string Local(string name, byte[] bytes)
    {
        var path = Path.Combine(_clientDir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private async Task<JsonElement> PrepareAsync(string vaultPath, byte[] bytes)
    {
        var result = await Wire.CallOk(_client, "vault_upload_prepare", new Dictionary<string, object?>
        {
            ["items"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["path"] = vaultPath, ["sourceSha256"] = Sha(bytes), ["byteLength"] = bytes.LongLength, ["mode"] = "create",
                },
            },
        });
        var item = result.GetProperty("items")[0];
        item.GetProperty("grant").ValueKind.ShouldBe(JsonValueKind.Object, item.ToString());
        return item.GetProperty("grant");
    }

    private (int Exit, string Stdout, string Stderr) RunSend(JsonElement grant, string src, string? urlOverride = null)
    {
        var psi = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _clientDir,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(grant.GetProperty("send").GetString()!); // literally, prose comments and all
        psi.Environment["TOKEN"] = grant.GetProperty("uploadToken").GetString();
        psi.Environment["URL"] = urlOverride ?? grant.GetProperty("uploadUrl").GetString()!
            .Replace(PlaceholderOrigin, $"http://127.0.0.1:{_server.Port}", StringComparison.Ordinal);
        psi.Environment["SRC"] = src;
        using var process = Process.Start(psi)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr.Result);
    }

    private async Task<JsonElement> CommitAsync(JsonElement grant) =>
        (await Wire.CallOk(_client, "vault_upload_commit", new Dictionary<string, object?>
        {
            ["uploadIds"] = new[] { grant.GetProperty("uploadId").GetString() },
        })).GetProperty("items")[0];

    [Theory]
    [InlineData("empty.bin", 0)]
    [InlineData("note.md", 20_000)]
    [InlineData("photo.jpg", 2_900_000)]   // > 1 MiB: curl sends Expect: 100-continue
    [InlineData("ceiling.bin", 5 * 1024 * 1024)]
    public async Task The_send_recipe_runs_as_written_and_the_commit_lands_exact_bytes(string name, int size)
    {
        var bytes = name.EndsWith(".md", StringComparison.Ordinal)
            ? System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("line\r\n", size / 6)))
            : RandomNumberGenerator.GetBytes(size);
        var grant = await PrepareAsync($"Uploads/{name}", bytes);
        grant.GetProperty("send").GetString()!.ShouldContain("--proto '=http'");

        var (exit, stdout, stderr) = RunSend(grant, Local(name, bytes));

        exit.ShouldBe(0, stderr);
        JsonDocument.Parse(stdout).RootElement.GetProperty("state").GetString().ShouldBe("ready");
        File.Exists(Path.Combine(_vaultDir, "Uploads", name)).ShouldBeFalse("the PUT only stages");

        var committed = await CommitAsync(grant);
        committed.GetProperty("state").GetString().ShouldBe("succeeded");
        committed.GetProperty("result").GetProperty("newSha256").GetString().ShouldBe(Sha(bytes));
        File.ReadAllBytes(Path.Combine(_vaultDir, "Uploads", name)).ShouldBe(bytes);
    }

    [Fact]
    public async Task A_file_changed_after_hashing_fails_with_a_readable_code_and_lands_nothing()
    {
        var hashed = RandomNumberGenerator.GetBytes(4096);
        var grant = await PrepareAsync("Uploads/changed.bin", hashed);
        var changed = (byte[])hashed.Clone();
        changed[0] ^= 0xFF;

        var (exit, stdout, _) = RunSend(grant, Local("changed.bin", changed));

        exit.ShouldBe(22); // --fail-with-body: the server refused, and said why
        JsonDocument.Parse(stdout).RootElement.GetProperty("code").GetString().ShouldBe("UploadHashMismatch");
        (await CommitAsync(grant)).GetProperty("state").GetString().ShouldBe("failed");
        File.Exists(Path.Combine(_vaultDir, "Uploads/changed.bin")).ShouldBeFalse();
    }

    [Fact]
    public async Task A_refused_connection_claims_nothing_so_the_same_grant_still_works()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var grant = await PrepareAsync("Uploads/retry.bin", bytes);
        var src = Local("retry.bin", bytes);

        var (refused, _, _) = RunSend(grant, src, urlOverride: grant.GetProperty("uploadUrl").GetString()); // port 9: closed
        refused.ShouldBe(7);

        RunSend(grant, src).Exit.ShouldBe(0);
        (await CommitAsync(grant)).GetProperty("state").GetString().ShouldBe("succeeded");
    }

    [Fact]
    public async Task A_second_send_of_a_used_grant_is_told_to_check_status()
    {
        var bytes = new byte[] { 4, 5, 6 };
        var grant = await PrepareAsync("Uploads/twice.bin", bytes);
        var src = Local("twice.bin", bytes);
        RunSend(grant, src).Exit.ShouldBe(0);

        var (exit, stdout, _) = RunSend(grant, src);
        exit.ShouldBe(22);
        JsonDocument.Parse(stdout).RootElement.GetProperty("code").GetString().ShouldBe("UploadAlreadyClaimed");
    }

    /// <summary>
    /// Review 2026-11-08: a malformed body on a still-usable connection (here
    /// an invalid chunk-size line, against real Kestrel) failed the grant and
    /// then answered 200 with an empty body. It must answer a non-success
    /// status, and the grant must read failed.
    /// </summary>
    [Fact]
    public async Task A_malformed_chunked_body_is_answered_400_never_200()
    {
        var grant = await PrepareAsync("Uploads/malformed.bin", [1, 2, 3]);
        var path = new Uri(grant.GetProperty("uploadUrl").GetString()!).AbsolutePath;

        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync("127.0.0.1", _server.Port);
        var stream = tcp.GetStream();
        var request =
            $"PUT {path} HTTP/1.1\r\nHost: 127.0.0.1:{_server.Port}\r\n" +
            $"Authorization: KnapperUpload {grant.GetProperty("uploadToken").GetString()}\r\n" +
            "Content-Type: application/octet-stream\r\nTransfer-Encoding: chunked\r\n\r\nZZ\r\n";
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(request));
        using var reader = new StreamReader(stream);
        var statusLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));

        statusLine.ShouldNotBeNull().ShouldStartWith("HTTP/1.1 400");

        var status = await Wire.CallOk(_client, "vault_upload_status", new Dictionary<string, object?>
        {
            ["uploadIds"] = new[] { grant.GetProperty("uploadId").GetString() },
        });
        status.GetProperty("items")[0].GetProperty("state").GetString().ShouldBe("failed");
    }
}
