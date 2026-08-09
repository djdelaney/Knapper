using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Knapper.Mcp.Tests;

/// <summary>
/// The production topology is <c>cloudflared → http://127.0.0.1:3535</c>:
/// every internet request arrives at Knapper from a LOOPBACK TCP peer while
/// carrying its PUBLIC Host header. These tests pin the rule that the
/// loopback exemption requires loopback peer AND loopback Host — a tunneled
/// request must present a valid owner assertion no matter what its TCP peer
/// looks like, for both MCP and /health.
/// </summary>
public sealed class AccessTopologyTests : IDisposable
{
    private const string PublicHost = "mcp.example.com";
    private const string TeamDomain = "https://knapper-test.cloudflareaccess.com";
    private const string OwnerAudience = "aud-owner-test";
    private const string MonitoringAudience = "aud-monitor-test";

    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "knapper-test-key" };

    private readonly AccessEnabledFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    // ---- the P0 regression: tunneled request without an assertion --------

    [Fact]
    public async Task Mcp_from_loopback_peer_with_public_host_and_no_assertion_is_rejected()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/");
        request.Headers.Host = PublicHost;
        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Mcp_from_loopback_peer_with_public_host_and_garbage_assertion_is_rejected()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/");
        request.Headers.Host = PublicHost;
        request.Headers.Add(AccessAuth.AssertionHeader, "not-a-jwt");
        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Mcp_rejects_an_expired_owner_assertion()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/");
        request.Headers.Host = PublicHost;
        request.Headers.Add(AccessAuth.AssertionHeader,
            Mint(OwnerAudience, email: "owner@example.com", expires: DateTime.UtcNow.AddMinutes(-10)));
        var response = await client.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Health_is_not_reachable_through_the_tunnel_with_or_without_owner_auth()
    {
        using var client = _factory.CreateClient();

        // No assertion: the owner policy denies before the handler runs.
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/health"))
        {
            request.Headers.Host = PublicHost;
            (await client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // Even a VALID owner assertion doesn't open /health through the
        // tunnel: the detailed body (paths, conflict names) is same-box only.
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/health"))
        {
            request.Headers.Host = PublicHost;
            request.Headers.Add(AccessAuth.AssertionHeader, Mint(OwnerAudience, email: "owner@example.com"));
            (await client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }

    // ---- what must keep working ------------------------------------------

    [Fact]
    public async Task Genuine_local_caller_loopback_peer_and_loopback_host_needs_no_assertion()
    {
        using var client = _factory.CreateClient(); // Host defaults to localhost
        (await client.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/up")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Valid_owner_assertion_reaches_tools_and_lands_its_email_in_the_audit_log()
    {
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Host = PublicHost;
        http.DefaultRequestHeaders.Add(AccessAuth.AssertionHeader, Mint(OwnerAudience, email: "owner@example.com"));
        await using (var client = await McpSurfaceTests.ConnectAsync(_factory, http))
        {
            var sha = (await McpSurfaceTests.CallOk(client, "vault_read", new() { ["path"] = "Notes/Daily.md" }))
                .GetProperty("sha256").GetString();
            await McpSurfaceTests.CallOk(client, "vault_edit", new()
            {
                ["path"] = "Notes/Daily.md",
                ["expectSha256"] = sha,
                ["edits"] = new[] { new { old = "TODO alpha", @new = "DONE alpha" } },
            });
        }

        AuditClients().ShouldContain("owner@example.com");
    }

    [Fact]
    public async Task Valid_service_token_assertion_lands_its_common_name_in_the_audit_log()
    {
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Host = PublicHost;
        http.DefaultRequestHeaders.Add(AccessAuth.AssertionHeader,
            Mint(OwnerAudience, commonName: "claude-code-service-token"));
        await using (var client = await McpSurfaceTests.ConnectAsync(_factory, http))
        {
            await McpSurfaceTests.CallOk(client, "vault_read", new() { ["path"] = "Notes/Daily.md" });
            var sha = (await McpSurfaceTests.CallOk(client, "vault_read", new() { ["path"] = "Notes/Sub/Deep.md" }))
                .GetProperty("sha256").GetString();
            await McpSurfaceTests.CallOk(client, "vault_edit", new()
            {
                ["path"] = "Notes/Sub/Deep.md",
                ["expectSha256"] = sha,
                ["edits"] = new[] { new { old = "deep needle", @new = "deep threaded" } },
            });
        }

        AuditClients().ShouldContain("claude-code-service-token");
    }

    [Fact]
    public async Task Monitoring_audience_reaches_up_but_never_the_vault_surface()
    {
        using var client = _factory.CreateClient();
        var token = Mint(MonitoringAudience, commonName: "monitor");

        using (var request = new HttpRequestMessage(HttpMethod.Get, "/up"))
        {
            request.Headers.Host = PublicHost;
            request.Headers.Add(AccessAuth.AssertionHeader, token);
            (await client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // A leaked monitoring credential must not reach MCP: the token is
        // VALID for the deployment (authenticates), but the owner policy
        // rejects its audience — 403, not 401.
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/"))
        {
            request.Headers.Host = PublicHost;
            request.Headers.Add(AccessAuth.AssertionHeader, token);
            (await client.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    // ---- plumbing --------------------------------------------------------

    private IEnumerable<string?> AuditClients()
    {
        var auditPath = Path.Combine(_factory.OutsideDir, "audit.jsonl");
        return File.ReadAllLines(auditPath)
            .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement.GetProperty("Client").GetString())
            .ToList();
    }

    private static string Mint(
        string audience, string? email = null, string? commonName = null, DateTime? expires = null)
    {
        var claims = new Dictionary<string, object>();
        if (email is not null)
            claims["email"] = email;
        if (commonName is not null)
            claims["common_name"] = commonName;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = TeamDomain,
            Audience = audience,
            Claims = claims,
            IssuedAt = expires is null ? null : DateTime.UtcNow.AddMinutes(-20),
            NotBefore = expires is null ? null : DateTime.UtcNow.AddMinutes(-20),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
        });
    }

    /// <summary>
    /// The real server with Access ENABLED. The JWKS fetch is replaced with a
    /// static in-memory key so startup key verification and request
    /// validation run for real without touching Cloudflare.
    /// </summary>
    private sealed class AccessEnabledFactory : KnapperMcpFactory
    {
        internal AccessEnabledFactory() : base(new Dictionary<string, string?>
        {
            ["Mcp:Access:Enabled"] = "true",
            ["Mcp:Access:TeamDomain"] = TeamDomain,
            ["Mcp:Access:Audience"] = OwnerAudience,
            ["Mcp:Access:MonitoringAudience"] = MonitoringAudience,
            ["Mcp:AllowedHosts:0"] = PublicHost,
        })
        {
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    var configuration = new OpenIdConnectConfiguration { Issuer = TeamDomain };
                    configuration.SigningKeys.Add(SigningKey);
                    options.ConfigurationManager =
                        new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                }));
        }
    }
}
