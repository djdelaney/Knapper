using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Knapper.Mcp.Tests;

/// <summary>
/// The <c>/transfer</c> prefix through the real ASP.NET pipeline
/// (transfer-grants §5.2, §9, §14), in local-only mode so TestServer's
/// loopback caller holds the local principal.
/// </summary>
public class TransferEndpointTests : IClassFixture<TransferEndpointTests.LocalOnlyFactory>
{
    public sealed class LocalOnlyFactory() : KnapperMcpFactory(new Dictionary<string, string?>
    {
        ["Transfer:LocalOnly"] = "true",
        ["Transfer:PublicOrigin"] = "http://127.0.0.1:3535",
        ["Download:Enabled"] = "true",
        ["Download:AllowedPrefixes:0"] = "Photos",
    });

    private readonly LocalOnlyFactory _factory;

    public TransferEndpointTests(LocalOnlyFactory factory)
    {
        _factory = factory;
    }

    private async Task<JsonElement> PrepareAsync(string path)
    {
        var session = await RawMcp.OpenAsync(_factory.CreateClient());
        var result = await session.CallToolAsync("vault_download_prepare", new { items = new[] { new { path } } });
        return result.GetProperty("structuredContent").GetProperty("items")[0];
    }

    private static HttpRequestMessage Get(string url, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url).PathAndQuery);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("KnapperDownload", token);
        return request;
    }

    private static async Task<string> CodeOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString()!;

    [Fact]
    public async Task A_grant_serves_the_exact_bytes_once_and_then_refuses()
    {
        var bytes = RandomNumberGenerator.GetBytes(70_000);
        File.WriteAllBytes(Path.Combine(_factory.VaultDir, "Photos", "once.bin").EnsureParent(), bytes);
        var grant = (await PrepareAsync("Photos/once.bin")).GetProperty("grant");
        var http = _factory.CreateClient();

        var request = Get(grant.GetProperty("downloadUrl").GetString()!, grant.GetProperty("downloadToken").GetString());
        request.Headers.Range = new RangeHeaderValue(0, 9);          // ignored: always the full 200
        request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"x\"")); // ignored: never a 304
        var response = await http.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(bytes);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/octet-stream");
        response.Content.Headers.ContentLength.ShouldBe(bytes.Length);
        response.Content.Headers.ContentDisposition!.DispositionType.ShouldBe("attachment");
        response.Content.Headers.ContentDisposition.FileName.ShouldBeNull(); // the vault path never reaches a header
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        response.Headers.ETag.ShouldBeNull();
        response.Content.Headers.LastModified.ShouldBeNull();
        response.Headers.GetValues("X-Content-Type-Options").ShouldContain("nosniff");
        grant.GetProperty("sha256").GetString().ShouldBe(Convert.ToHexStringLower(SHA256.HashData(bytes)));

        var again = await http.SendAsync(Get(grant.GetProperty("downloadUrl").GetString()!, grant.GetProperty("downloadToken").GetString()));
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await CodeOf(again)).ShouldBe("DownloadAlreadyClaimed");
    }

    [Fact]
    public async Task A_missing_or_wrong_token_is_401_with_the_grant_scheme_and_burns_nothing()
    {
        _factory.Seed("Photos/guarded.txt", "guarded");
        var grant = (await PrepareAsync("Photos/guarded.txt")).GetProperty("grant");
        var url = grant.GetProperty("downloadUrl").GetString()!;
        var http = _factory.CreateClient();

        foreach (var token in new string?[] { null, "garbage", new string('A', 43) })
        {
            var refused = await http.SendAsync(Get(url, token));
            refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            refused.Headers.WwwAuthenticate.ToString().ShouldBe("KnapperDownload");
            (await CodeOf(refused)).ShouldBe("DownloadInvalidToken");
        }
        var bearer = Get(url, null);
        bearer.Headers.Authorization = new AuthenticationHeaderValue("Bearer", grant.GetProperty("downloadToken").GetString());
        (await http.SendAsync(bearer)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await http.SendAsync(Get(url, grant.GetProperty("downloadToken").GetString()))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Downloads_accept_GET_only_and_other_methods_claim_nothing(string method)
    {
        _factory.Seed("Photos/methods.txt", "m");
        var grant = (await PrepareAsync("Photos/methods.txt")).GetProperty("grant");
        var url = new Uri(grant.GetProperty("downloadUrl").GetString()!).PathAndQuery;
        var token = grant.GetProperty("downloadToken").GetString();
        var http = _factory.CreateClient();

        var request = new HttpRequestMessage(new HttpMethod(method), url);
        request.Headers.Authorization = new AuthenticationHeaderValue("KnapperDownload", token);
        var refused = await http.SendAsync(request);
        refused.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
        refused.Content.Headers.Allow.ShouldContain("GET");

        (await http.SendAsync(Get(url, token))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// The edge's Bypass sends everything under /transfer here
    /// unauthenticated, including the encoded-slash request it does NOT
    /// resolve (measured 2026-11-08). Each must die in the transfer
    /// catch-all — never MCP, /health or /up.
    /// </summary>
    [Theory]
    [InlineData("/transfer")]
    [InlineData("/transfer/")]
    [InlineData("/transfer/..%2Fmcp")]
    [InlineData("/transfer/downloads")]
    [InlineData("/transfer/downloads/")]
    [InlineData("/transfer/downloads/a/b")]
    [InlineData("/transfer/health")]
    [InlineData("/transfer/up")]
    [InlineData("/transfer/downloads/..%2F..%2Fhealth")]
    public async Task Every_other_path_under_the_prefix_is_a_transfer_404(string path)
    {
        var response = await _factory.CreateClient().GetAsync(path);
        response.StatusCode.ShouldBe(path.StartsWith("/transfer/downloads/..", StringComparison.Ordinal)
            ? HttpStatusCode.Unauthorized   // a well-shaped route with an unknown ID
            : HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("jsonrpc");
    }

    [Fact]
    public async Task Uploads_are_refused_without_reading_the_body()
    {
        var http = _factory.CreateClient();
        var put = await http.PutAsync("/transfer/uploads/" + new string('A', 43), new ByteArrayContent(new byte[1024]));
        put.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOf(put)).ShouldBe("UploadDisabled");

        var get = await http.GetAsync("/transfer/uploads/" + new string('A', 43));
        get.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task A_path_outside_the_allowlist_issues_nothing()
    {
        var item = await PrepareAsync("Notes/Daily.md");
        item.GetProperty("grant").ValueKind.ShouldBe(JsonValueKind.Null);
        item.GetProperty("errorCode").GetString().ShouldBe("DownloadNotPermitted");
    }

    [Fact]
    public void Nothing_but_the_transfer_catch_all_is_mapped_under_the_prefix()
    {
        _ = _factory.CreateClient();
        var routes = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? "")
            .Where(p => p.TrimStart('/').StartsWith("transfer", StringComparison.OrdinalIgnoreCase))
            .ToList();
        routes.ShouldHaveSingleItem().ShouldBe("/transfer/{**rest}");
    }
}

public class TransferDisabledTests : IClassFixture<KnapperMcpFactory>
{
    private readonly KnapperMcpFactory _factory;

    public TransferDisabledTests(KnapperMcpFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task A_disabled_direction_refuses_prepare_and_route_consistently()
    {
        var session = await RawMcp.OpenAsync(_factory.CreateClient());
        var prepare = await session.CallToolAsync("vault_download_prepare", new { items = new[] { new { path = "Notes/Daily.md" } } });
        prepare.GetProperty("isError").GetBoolean().ShouldBeTrue();
        prepare.ToString().ShouldContain("[DownloadDisabled]");

        var get = await _factory.CreateClient().GetAsync("/transfer/downloads/" + new string('A', 43));
        get.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await get.Content.ReadAsStringAsync()).ShouldContain("DownloadDisabled");
    }

    [Fact]
    public async Task Upload_commit_is_never_longer_than_vault_create_so_it_fits_wherever_create_does()
    {
        // Both carry the same convention clauses (writing + placement), so a
        // deployment whose composed vault_create fits the delivery budget is
        // guaranteed to fit vault_upload_commit too — compared on the SERVED
        // strings, after composition.
        var tools = await (await RawMcp.OpenAsync(_factory.CreateClient())).ListToolsAsync();
        string Description(string name) =>
            tools.Single(t => t.GetProperty("name").GetString() == name).GetProperty("description").GetString()!;
        Description("vault_upload_commit").Length.ShouldBeLessThanOrEqualTo(Description("vault_create").Length);
    }
}

public class TransferStartupRefusalTests
{
    [Theory]
    [InlineData("Upload:MaxConcurrentReceives", "0")]
    [InlineData("Mcp:DisabledTools:0", "vault_upload_commit")]
    [InlineData("Mcp:ReadOnly", "true")]
    [InlineData("Mcp:DisabledTools:0", "vault_download_prepare")]
    [InlineData("Transfer:PublicOrigin", "http://vault.example.com")]
    public void An_incoherent_transfer_configuration_refuses_to_boot(string key, string value)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Transfer:LocalOnly"] = "true",
            ["Transfer:PublicOrigin"] = "http://127.0.0.1:3535",
            ["Download:Enabled"] = "true",
            ["Download:AllowedPrefixes:0"] = "Photos",
            ["Upload:Enabled"] = "true",
            [key] = value,
        };
        using var factory = new KnapperMcpFactory(settings);
        var ex = Should.Throw<Exception>(() => factory.CreateClient());
        ex.ToString().ShouldContain("Transfer configuration refused");
    }

    [Fact]
    public async Task Without_local_only_an_unauthenticated_loopback_caller_has_no_principal()
    {
        // Access off and not local-only: the dev shape. A loopback caller is
        // NOT the owner just because it is local.
        using var factory = new KnapperMcpFactory(new Dictionary<string, string?>
        {
            ["Transfer:PublicOrigin"] = "https://127.0.0.1:3535",
            ["Download:Enabled"] = "true",
            ["Download:AllowedPrefixes:0"] = "Notes",
        });
        var session = await RawMcp.OpenAsync(factory.CreateClient());
        var result = await session.CallToolAsync("vault_download_prepare", new { items = new[] { new { path = "Notes/Daily.md" } } });
        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        result.ToString().ShouldContain("[DownloadNotPermitted]");
    }
}

internal static class PathTestExtensions
{
    internal static string EnsureParent(this string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}
