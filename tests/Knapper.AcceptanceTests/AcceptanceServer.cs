using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;

namespace Knapper.AcceptanceTests;

/// <summary>
/// A REAL Knapper server: the published Knapper.Mcp binary spawned as its own
/// process (`dotnet exec`, ephemeral port, env-var config), spoken to over a
/// real TCP socket by the SDK's Streamable HTTP client. Nothing in this
/// project loads Knapper types in-process — the brief's §13 definition of
/// done is about the deployed shape: separate processes, real Kestrel, real
/// flock, real ripgrep. Two servers built over the same vault + lock
/// directory ARE the two-process topology.
/// </summary>
public sealed class AcceptanceServer : IDisposable
{
    private static int _instance;

    private readonly Process _process;
    private readonly StringBuilder _output = new();

    public int Port { get; }
    public string VaultDir { get; }

    public AcceptanceServer(string vaultDir, string outsideDir, IDictionary<string, string>? extraEnv = null)
    {
        VaultDir = vaultDir;
        Port = FreePort();
        var dll = Path.Combine(AppContext.BaseDirectory, "Knapper.Mcp.dll");
        File.Exists(dll).ShouldBeTrue($"server binary not found at {dll}");

        // Locks are SHARED across instances over the same outsideDir — that
        // is the cross-process contract under test. The audit/metrics files
        // are per-instance: .NET emulates FileShare with flock on Unix, so
        // two processes appending one audit file would contend at open time
        // and fail mutations for reasons that are artifacts of the harness.
        var n = Interlocked.Increment(ref _instance);
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = AppContext.BaseDirectory, // content root: appsettings.json lives here
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(dll);
        psi.Environment["Vault__RootPath"] = vaultDir;
        psi.Environment["Vault__LockDirectory"] = Path.Combine(outsideDir, "locks");
        psi.Environment["Vault__AuditLogPath"] = Path.Combine(outsideDir, $"audit-{n}.jsonl");
        psi.Environment["Vault__MetricsPath"] = Path.Combine(outsideDir, $"metrics-{n}.json");
        psi.Environment["Sync__Mode"] = "open";
        psi.Environment["Mcp__BindAddress"] = "127.0.0.1";
        psi.Environment["Mcp__Port"] = Port.ToString();
        psi.Environment["Mcp__LogToolCalls"] = "false";
        psi.Environment["Logging__LogLevel__Default"] = "Warning";
        foreach (var (key, value) in extraEnv ?? new Dictionary<string, string>())
            psi.Environment[key] = value;

        _process = Process.Start(psi)!;
        _process.OutputDataReceived += (_, e) => Collect(e.Data);
        _process.ErrorDataReceived += (_, e) => Collect(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        WaitForUp();
    }

    public string Output
    {
        get
        {
            lock (_output)
                return _output.ToString();
        }
    }

    /// <summary>A real HTTP MCP client over the real socket — the path Claude uses.</summary>
    public async Task<McpClient> ConnectAsync()
    {
        var endpoint = new Uri($"http://127.0.0.1:{Port}/");
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = endpoint },
            new HttpClient { BaseAddress = endpoint });
        return await McpClient.CreateAsync(transport);
    }

    public async Task<HttpStatusCode> UpStatusAsync()
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync($"http://127.0.0.1:{Port}/up");
        return response.StatusCode;
    }

    private void Collect(string? line)
    {
        if (line is null)
            return;
        lock (_output)
            _output.AppendLine(line);
    }

    private void WaitForUp()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(deadline) < TimeSpan.FromSeconds(30))
        {
            if (_process.HasExited)
                throw new InvalidOperationException($"server exited during startup:\n{Output}");
            try
            {
                using var response = http.GetAsync($"http://127.0.0.1:{Port}/up").GetAwaiter().GetResult();
                if (response.StatusCode == HttpStatusCode.OK)
                    return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            Thread.Sleep(100);
        }
        throw new TimeoutException($"server on port {Port} never became healthy:\n{Output}");
    }

    internal static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5_000);
            }
        }
        catch (InvalidOperationException) { }
        _process.Dispose();
    }
}

/// <summary>Wire-level helpers: seed vaults on disk, call tools, classify results.</summary>
internal static class Wire
{
    public static string NewTempDir(string prefix) =>
        Directory.CreateTempSubdirectory(prefix).FullName;

    public static void Seed(string vaultDir, string relative, string content)
    {
        var path = Path.Combine(vaultDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public static async Task<JsonElement> CallOk(McpClient client, string tool, Dictionary<string, object?> args)
    {
        var result = await client.CallToolAsync(tool, args);
        (result.IsError ?? false).ShouldBeFalse($"{tool} unexpectedly errored: {ErrorText(result)}");
        result.StructuredContent.ShouldNotBeNull();
        return result.StructuredContent!.Value;
    }

    public static async Task<string> CallError(McpClient client, string tool, Dictionary<string, object?> args)
    {
        var result = await client.CallToolAsync(tool, args);
        (result.IsError ?? false).ShouldBeTrue($"{tool} should have errored");
        return ErrorText(result);
    }

    /// <summary>For races: never asserts — returns ok-with-payload or the error text.</summary>
    public static async Task<(bool Ok, JsonElement? Payload, string Error)> Call(
        McpClient client, string tool, Dictionary<string, object?> args)
    {
        var result = await client.CallToolAsync(tool, args);
        return result.IsError ?? false
            ? (false, null, ErrorText(result))
            : (true, result.StructuredContent!.Value, "");
    }

    public static async Task<string> ReadSha(McpClient client, string relative) =>
        (await CallOk(client, "vault_read", new() { ["path"] = relative })).GetProperty("sha256").GetString()!;

    private static string ErrorText(ModelContextProtocol.Protocol.CallToolResult result) =>
        string.Join(" | ", result.Content
            .OfType<ModelContextProtocol.Protocol.TextContentBlock>()
            .Select(c => c.Text));

    public static void TryDeleteDir(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
