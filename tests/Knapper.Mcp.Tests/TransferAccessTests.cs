using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
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
/// Transfer grants in the PRODUCTION topology: Access enabled, every request
/// from cloudflared's loopback peer carrying the public Host
/// (transfer-grants §8, §9). The /transfer prefix must be answered by the
/// transfer handler with no assertion (the edge Bypass sends none), while the
/// grant itself can only be minted by an authenticated owner and is bound to
/// the assertion's subject.
/// </summary>
public sealed class TransferAccessTests : IDisposable
{
    private const string PublicHost = "mcp.example.com";
    private const string TeamDomain = "https://knapper-test.cloudflareaccess.com";
    private const string OwnerAudience = "aud-owner-test";
    private static readonly RsaSecurityKey SigningKey = new(RSA.Create(2048)) { KeyId = "knapper-transfer-key" };

    private readonly AccessFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private HttpClient Tunnelled(string? assertion)
    {
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Host = PublicHost;
        if (assertion is not null)
            http.DefaultRequestHeaders.Add(AccessAuth.AssertionHeader, assertion);
        return http;
    }

    [Fact]
    public async Task The_transfer_route_answers_without_an_assertion_and_refuses_by_grant_scheme()
    {
        var response = await Tunnelled(null).GetAsync("/transfer/downloads/" + new string('A', 43));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldBe("KnapperDownload"); // the handler, not JwtBearer
        (await response.Content.ReadAsStringAsync()).ShouldContain("DownloadInvalidToken");
    }

    [Fact]
    public async Task A_bypassed_escape_never_reaches_the_mcp_endpoint()
    {
        var response = await Tunnelled(null).GetAsync("/transfer/..%2Fmcp");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).ShouldContain("\"code\":\"NotFound\"");
    }

    [Fact]
    public async Task An_owner_assertion_mints_a_grant_that_serves_once_with_no_assertion()
    {
        _factory.Seed("Notes/photo.txt", "owner bytes");
        var session = await RawMcp.OpenAsync(Tunnelled(Mint(sub: "user-123", email: "owner@example.com")));
        var result = await session.CallToolAsync("vault_download_prepare", new { items = new[] { new { path = "Notes/photo.txt" } } });
        var grant = result.GetProperty("structuredContent").GetProperty("items")[0].GetProperty("grant");
        grant.GetProperty("downloadUrl").GetString()!.ShouldStartWith("https://mcp.example.com/transfer/downloads/");

        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(grant.GetProperty("downloadUrl").GetString()!).PathAndQuery);
        request.Headers.Authorization = new AuthenticationHeaderValue("KnapperDownload", grant.GetProperty("downloadToken").GetString());
        var response = await Tunnelled(null).SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe("owner bytes");

        var audit = File.ReadAllLines(Path.Combine(_factory.OutsideDir, "audit.jsonl"))
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Single(e => e.GetProperty("Op").GetString() == "download_prepare");
        audit.GetProperty("Principal").GetString().ShouldBe($"sub:user-123@{TeamDomain}");
        audit.GetProperty("Client").GetString().ShouldBe("owner@example.com"); // attribution, not the key
    }

    [Fact]
    public async Task A_service_token_assertion_is_a_principal_too()
    {
        _factory.Seed("Notes/svc.txt", "svc");
        var session = await RawMcp.OpenAsync(Tunnelled(Mint(commonName: "svc-client.access")));
        var result = await session.CallToolAsync("vault_download_prepare", new { items = new[] { new { path = "Notes/svc.txt" } } });
        result.GetProperty("structuredContent").GetProperty("items")[0].GetProperty("grant").ValueKind
            .ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public async Task The_loopback_exemption_supplies_no_principal()
    {
        // With Access:AllowLoopbackMcp, a genuine same-box caller passes the
        // loopback exemption with no claims at all — and therefore cannot
        // mint a grant.
        var session = await RawMcp.OpenAsync(_factory.CreateClient());
        var result = await session.CallToolAsync("vault_download_prepare", new { items = new[] { new { path = "Notes/Daily.md" } } });
        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        result.ToString().ShouldContain("[DownloadNotPermitted]");
    }

    /// <summary>
    /// An upload is bound to the principal that prepared it (§8.1): another
    /// verified identity — here a service token — cannot see or commit it.
    /// The PUT itself carries no assertion, exactly as through the Bypass.
    /// </summary>
    [Fact]
    public async Task An_upload_commits_only_for_the_principal_that_prepared_it()
    {
        var bytes = "uploaded over the edge\n"u8.ToArray();
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var owner = await RawMcp.OpenAsync(Tunnelled(Mint(sub: "user-123")));
        var prepared = await owner.CallToolAsync("vault_upload_prepare", new
        {
            items = new[] { new { path = "Notes/edge.md", sourceSha256 = sha, byteLength = (long)bytes.Length, mode = "create" } },
        });
        var grant = prepared.GetProperty("structuredContent").GetProperty("items")[0].GetProperty("grant");
        grant.GetProperty("uploadUrl").GetString()!.ShouldStartWith("https://mcp.example.com/transfer/uploads/");

        var put = new HttpRequestMessage(HttpMethod.Put, new Uri(grant.GetProperty("uploadUrl").GetString()!).PathAndQuery)
        {
            Content = new ByteArrayContent(bytes),
        };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        put.Headers.Authorization = new AuthenticationHeaderValue("KnapperUpload", grant.GetProperty("uploadToken").GetString());
        (await Tunnelled(null).SendAsync(put)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var id = grant.GetProperty("uploadId").GetString();
        var stranger = await RawMcp.OpenAsync(Tunnelled(Mint(commonName: "svc-client.access")));
        var theirs = await stranger.CallToolAsync("vault_upload_commit", new { uploadIds = new[] { id } });
        theirs.GetProperty("structuredContent").GetProperty("items")[0].GetProperty("errorCode").GetString()
            .ShouldBe("UploadNotFound");
        File.Exists(Path.Combine(_factory.VaultDir, "Notes/edge.md")).ShouldBeFalse();

        var mine = await owner.CallToolAsync("vault_upload_commit", new { uploadIds = new[] { id } });
        mine.GetProperty("structuredContent").GetProperty("items")[0].GetProperty("state").GetString().ShouldBe("succeeded");
        _factory.ReadVaultFile("Notes/edge.md").ShouldBe("uploaded over the edge\n");
    }

    private static string Mint(string? sub = null, string? email = null, string? commonName = null)
    {
        var claims = new Dictionary<string, object>();
        if (sub is not null)
            claims["sub"] = sub;
        if (email is not null)
            claims["email"] = email;
        if (commonName is not null)
        {
            claims["common_name"] = commonName;
            claims["sub"] = ""; // Access service-token assertions carry an empty subject
        }
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = TeamDomain,
            Audience = OwnerAudience,
            Claims = claims,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
        });
    }

    private sealed class AccessFactory : KnapperMcpFactory
    {
        internal AccessFactory() : base(new Dictionary<string, string?>
        {
            ["Mcp:Access:Enabled"] = "true",
            ["Mcp:Access:TeamDomain"] = TeamDomain,
            ["Mcp:Access:Audience"] = OwnerAudience,
            // Opted in so The_loopback_exemption_supplies_no_principal can
            // reach a tool at all: by default a same-box caller is refused at
            // the MCP endpoint before any grant question arises.
            ["Mcp:Access:AllowLoopbackMcp"] = "true",
            ["Mcp:AllowedHosts:0"] = PublicHost,
            ["Transfer:PublicOrigin"] = "https://" + PublicHost,
            ["Download:Enabled"] = "true",
            ["Download:AllowedPrefixes:0"] = "Notes",
            ["Upload:Enabled"] = "true",
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

    /// <summary>
    /// Review 2026-11-08: a served transfer and a refused re-use are recorded
    /// against the grant (§11), so a completed export is distinguishable
    /// from a prepare nobody fetched.
    /// </summary>
    [Fact]
    public async Task Transfer_outcomes_are_audited_against_the_grant()
    {
        _factory.Seed("Notes/outcome.txt", "outcome");
        var session = await RawMcp.OpenAsync(Tunnelled(Mint(sub: "user-9")));
        var result = await session.CallToolAsync("vault_download_prepare", new { items = new[] { new { path = "Notes/outcome.txt" } } });
        var grant = result.GetProperty("structuredContent").GetProperty("items")[0].GetProperty("grant");
        var url = new Uri(grant.GetProperty("downloadUrl").GetString()!).PathAndQuery;
        var token = grant.GetProperty("downloadToken").GetString();

        for (var i = 0; i < 2; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("KnapperDownload", token);
            await Tunnelled(null).SendAsync(request);
        }
        await Tunnelled(null).GetAsync("/transfer/downloads/" + new string('A', 43)); // a probe: never audited

        var outcomes = File.ReadAllLines(Path.Combine(_factory.OutsideDir, "audit.jsonl"))
            .Select(l => JsonDocument.Parse(l).RootElement)
            .Where(e => e.GetProperty("Op").GetString() == "download_transfer")
            .ToList();
        outcomes.Select(e => e.GetProperty("Outcome").GetString()).ShouldBe(["sent", "DownloadAlreadyClaimed"]);
        outcomes.ShouldAllBe(e => e.GetProperty("GrantId").GetString() == grant.GetProperty("downloadId").GetString());
        outcomes.ShouldAllBe(e => e.GetProperty("Principal").GetString() == $"sub:user-9@{TeamDomain}");
        outcomes[0].GetProperty("ByteLength").GetInt64().ShouldBe(7);
        outcomes[0].GetProperty("Path").GetString().ShouldBe("Notes/outcome.txt");
        File.ReadAllText(Path.Combine(_factory.OutsideDir, "audit.jsonl")).ShouldNotContain(token!);
    }
}
