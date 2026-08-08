using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Knapper.Mcp.Tests;

/// <summary>
/// The real ASP.NET Core MCP server in-process over a generated temp vault.
/// Settings are applied through BOTH UseSetting and in-memory configuration:
/// tool registration reads builder.Configuration at builder time, options
/// bind later — neither mechanism alone covers both halves of Program.cs.
/// </summary>
public class KnapperMcpFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _extraSettings;

    public string VaultDir { get; }
    public string OutsideDir { get; }

    public KnapperMcpFactory() : this(null) { }

    // Internal: xunit class fixtures allow exactly one PUBLIC constructor.
    internal KnapperMcpFactory(Dictionary<string, string?>? extraSettings)
    {
        _extraSettings = extraSettings ?? [];
        VaultDir = Directory.CreateTempSubdirectory("knapper-mcp-vault-").FullName;
        OutsideDir = Directory.CreateTempSubdirectory("knapper-mcp-outside-").FullName;
        Seed("Notes/Daily.md", "# Daily\nTODO alpha\nDone beta\n");
        Seed("Notes/Sub/Deep.md", "deep needle content\n");
        Seed("Projects/plan.md", "---\nstatus: active\n---\nneedle plan\n");
    }

    public void Seed(string relative, string content)
    {
        var path = Path.Combine(VaultDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public string ReadVaultFile(string relative) => File.ReadAllText(Path.Combine(VaultDir, relative));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Vault:RootPath"] = VaultDir,
            ["Vault:LockDirectory"] = Path.Combine(OutsideDir, "locks"),
            ["Vault:AuditLogPath"] = Path.Combine(OutsideDir, "audit.jsonl"),
            ["Sync:Mode"] = "open",
        };
        foreach (var (key, value) in _extraSettings)
            settings[key] = value;

        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));

        // Declare this factory to be the loopback caller it stands in for.
        // TestServer leaves RemoteIpAddress null, and both the /health
        // restriction and the Access loopback exemption fail closed on null.
        builder.ConfigureTestServices(services =>
            services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(IPAddress.Loopback)));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        TryDelete(VaultDir);
        TryDelete(OutsideDir);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Stamps a remote address at the front of the pipeline — TestServer
/// synthesises requests with a null RemoteIpAddress, a state no real
/// deployment is in, and every loopback-sensitive control here fails closed
/// on null.
/// </summary>
internal sealed class RemoteIpStartupFilter(IPAddress remoteIp) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress = remoteIp;
            await nextMiddleware();
        });
        next(app);
    };
}
