using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Knapper.Mcp.Tests;

/// <summary>
/// The upload direction through the real ASP.NET pipeline (transfer-grants
/// §4.2, §14): prepare and commit over MCP, the bytes over
/// <c>PUT /transfer/uploads/{id}</c>, in local-only mode so TestServer's
/// loopback caller holds the local principal.
/// </summary>
public class UploadEndpointTests : IClassFixture<UploadEndpointTests.UploadFactory>
{
    public sealed class UploadFactory() : KnapperMcpFactory(new Dictionary<string, string?>
    {
        ["Transfer:LocalOnly"] = "true",
        ["Transfer:PublicOrigin"] = "http://127.0.0.1:3535",
        ["Upload:Enabled"] = "true",
        ["Upload:AllowReplace"] = "true",
    });

    private readonly UploadFactory _factory;

    public UploadEndpointTests(UploadFactory factory)
    {
        _factory = factory;
        Directory.CreateDirectory(Path.Combine(factory.VaultDir, "Uploads"));
    }

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private async Task<JsonElement> Tool(string name, object arguments)
    {
        var session = await RawMcp.OpenAsync(_factory.CreateClient());
        var result = await session.CallToolAsync(name, arguments);
        (result.TryGetProperty("isError", out var e) && e.GetBoolean()).ShouldBeFalse(result.ToString());
        return result.GetProperty("structuredContent");
    }

    private async Task<JsonElement> PrepareAsync(string path, byte[] bytes, string mode = "create", string? expect = null)
    {
        var content = await Tool("vault_upload_prepare", new
        {
            items = new[] { new { path, sourceSha256 = Sha(bytes), byteLength = (long)bytes.Length, mode, expectSha256 = expect } },
        });
        var item = content.GetProperty("items")[0];
        item.GetProperty("grant").ValueKind.ShouldBe(JsonValueKind.Object, item.ToString());
        return item.GetProperty("grant");
    }

    private static HttpRequestMessage Put(JsonElement grant, HttpContent body, string? token = null, string scheme = "KnapperUpload")
    {
        var request = new HttpRequestMessage(HttpMethod.Put, new Uri(grant.GetProperty("uploadUrl").GetString()!).PathAndQuery)
        {
            Content = body,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(scheme, token ?? grant.GetProperty("uploadToken").GetString());
        return request;
    }

    private static ByteArrayContent Octets(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    /// <summary>A body with no Content-Length, so only the stream can tell its true size.</summary>
    private static StreamContent Unsized(byte[] bytes)
    {
        var content = new StreamContent(new NonSeekableStream(bytes));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    private static async Task<string> CodeOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString()!;

    private async Task<string> StateOf(JsonElement grant) =>
        (await Tool("vault_upload_status", new { uploadIds = new[] { grant.GetProperty("uploadId").GetString() } }))
            .GetProperty("items")[0].GetProperty("state").GetString()!;

    [Fact]
    public async Task Prepare_put_commit_lands_exact_bytes()
    {
        var bytes = RandomNumberGenerator.GetBytes(300_000);
        var grant = await PrepareAsync("Uploads/photo.jpg", bytes);
        var http = _factory.CreateClient();

        var put = await http.SendAsync(Put(grant, Octets(bytes)));
        put.StatusCode.ShouldBe(HttpStatusCode.OK);
        var staged = JsonDocument.Parse(await put.Content.ReadAsStringAsync()).RootElement;
        staged.GetProperty("state").GetString().ShouldBe("ready");
        staged.GetProperty("sha256").GetString().ShouldBe(Sha(bytes));
        File.Exists(Path.Combine(_factory.VaultDir, "Uploads/photo.jpg")).ShouldBeFalse("a PUT stages; it never writes the vault");

        var committed = (await Tool("vault_upload_commit", new { uploadIds = new[] { grant.GetProperty("uploadId").GetString() } }))
            .GetProperty("items")[0];
        committed.GetProperty("state").GetString().ShouldBe("succeeded");
        committed.GetProperty("result").GetProperty("newSha256").GetString().ShouldBe(Sha(bytes));
        File.ReadAllBytes(Path.Combine(_factory.VaultDir, "Uploads/photo.jpg")).ShouldBe(bytes);
    }

    [Fact]
    public async Task A_replace_lands_through_the_conditional_write()
    {
        _factory.Seed("Uploads/doc.txt", "old\n");
        var bytes = "new contents\n"u8.ToArray();
        var grant = await PrepareAsync("Uploads/doc.txt", bytes, "replace", Sha("old\n"u8.ToArray()));
        (await _factory.CreateClient().SendAsync(Put(grant, Octets(bytes)))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await Tool("vault_upload_commit", new { uploadIds = new[] { grant.GetProperty("uploadId").GetString() } }))
            .GetProperty("items")[0].GetProperty("state").GetString().ShouldBe("succeeded");
        _factory.ReadVaultFile("Uploads/doc.txt").ShouldBe("new contents\n");
    }

    [Fact]
    public async Task A_bad_token_is_401_with_the_grant_scheme_and_burns_nothing()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var grant = await PrepareAsync("Uploads/guarded.bin", bytes);
        var http = _factory.CreateClient();

        foreach (var (token, scheme) in new[] { ("garbage", "KnapperUpload"), (new string('A', 43), "KnapperUpload"),
                     (grant.GetProperty("uploadToken").GetString()!, "Bearer") })
        {
            var refused = await http.SendAsync(Put(grant, Octets(bytes), token, scheme));
            refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            refused.Headers.WwwAuthenticate.ToString().ShouldBe("KnapperUpload");
            (await CodeOf(refused)).ShouldBe("UploadInvalidToken");
        }
        (await http.SendAsync(Put(grant, Octets(bytes)))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await http.SendAsync(Put(grant, Octets(bytes)))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_body_with_the_wrong_hash_is_refused_and_spends_the_grant()
    {
        var grant = await PrepareAsync("Uploads/changed.bin", [1, 2, 3]);
        var put = await _factory.CreateClient().SendAsync(Put(grant, Octets([1, 2, 4])));
        put.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(put)).ShouldBe("UploadHashMismatch");
        (await StateOf(grant)).ShouldBe("failed");
    }

    [Theory]
    [InlineData(4, true, HttpStatusCode.RequestEntityTooLarge)]  // declared excess
    [InlineData(2, true, HttpStatusCode.BadRequest)]             // declared short
    [InlineData(4, false, HttpStatusCode.RequestEntityTooLarge)] // streamed excess: a matching prefix is never staged
    [InlineData(2, false, HttpStatusCode.BadRequest)]            // streamed short
    public async Task Length_is_enforced_on_the_header_and_on_the_stream(int sent, bool sized, HttpStatusCode expected)
    {
        var grant = await PrepareAsync($"Uploads/len-{sent}-{sized}.bin", [1, 2, 3]);
        var body = Enumerable.Range(1, sent).Select(i => (byte)i).ToArray();

        var put = await _factory.CreateClient().SendAsync(Put(grant, sized ? Octets(body) : Unsized(body)));

        put.StatusCode.ShouldBe(expected);
        (await CodeOf(put)).ShouldBe("UploadSizeMismatch");
        (await StateOf(grant)).ShouldBe("failed");
    }

    [Fact]
    public async Task Only_opaque_octets_are_accepted()
    {
        var grant = await PrepareAsync("Uploads/typed.txt", "x"u8.ToArray());
        var body = new ByteArrayContent("x"u8.ToArray());
        body.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        var put = await _factory.CreateClient().SendAsync(Put(grant, body));
        put.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        (await StateOf(grant)).ShouldBe("failed");

        var gzip = await PrepareAsync("Uploads/gz.bin", "y"u8.ToArray());
        var encoded = Octets("y"u8.ToArray());
        encoded.Headers.ContentEncoding.Add("gzip");
        (await _factory.CreateClient().SendAsync(Put(gzip, encoded))).StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task A_zero_byte_upload_is_a_file()
    {
        var grant = await PrepareAsync("Uploads/empty.bin", []);
        (await _factory.CreateClient().SendAsync(Put(grant, Octets([])))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Tool("vault_upload_commit", new { uploadIds = new[] { grant.GetProperty("uploadId").GetString() } }))
            .GetProperty("items")[0].GetProperty("state").GetString().ShouldBe("succeeded");
        new FileInfo(Path.Combine(_factory.VaultDir, "Uploads/empty.bin")).Length.ShouldBe(0);
    }

    [Fact]
    public async Task Commit_before_the_bytes_arrive_consumes_nothing()
    {
        var grant = await PrepareAsync("Uploads/early.bin", [5]);
        var early = (await Tool("vault_upload_commit", new { uploadIds = new[] { grant.GetProperty("uploadId").GetString() } }))
            .GetProperty("items")[0];
        early.GetProperty("errorCode").GetString().ShouldBe("UploadNotReady");
        (await _factory.CreateClient().SendAsync(Put(grant, Octets([5])))).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => base.Position; set => throw new NotSupportedException(); }
    }
}
