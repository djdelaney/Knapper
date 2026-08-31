using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Knapper.Mcp.Tests;

/// <summary>
/// JSON-RPC over HTTP with no SDK in the path — the bytes a client actually
/// receives.
///
/// This is not pedantry. The SDK's client does not hand back what the server
/// sent: for a tool returning a scalar the server publishes the wrapped
/// <c>{"properties": {"result": …}}</c> outputSchema, while
/// <c>McpClientTool.ProtocolTool.OutputSchema</c> reports the UNWRAPPED inner
/// schema. A manifest or conformance check written against the client's view
/// is checking a document no client ever receives — it flags the wrapper
/// tools as broken (they are fine) and lets a real defect through in a
/// different shape from the one that reaches Claude Code. Schema and payload
/// must be compared at the SAME layer, and the wire is the layer that counts.
/// </summary>
internal sealed class RawMcp
{
    private readonly HttpClient _http;

    private RawMcp(HttpClient http) => _http = http;

    internal static async Task<RawMcp> OpenAsync(HttpClient http)
    {
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var session = new RawMcp(http);
        await session.RequestAsync("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18",
            "capabilities":{},"clientInfo":{"name":"knapper-raw-probe","version":"1.0"}}}
            """);
        await session.PostAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        return session;
    }

    /// <summary>
    /// A raw <c>server/discover</c> result. Not a curiosity: this is how the
    /// claude.ai relay learns what this server is. Measured on CT 106 over 14
    /// days, that client sent 613 server/discover requests and exactly ONE
    /// initialize — seven days before the release whose instructions were
    /// being measured — so for the surface carrying most of the traffic,
    /// discover is the ONLY channel the instructions travel down.
    /// </summary>
    /// <remarks>
    /// The per-request <c>_meta</c> protocol version is REQUIRED here — the
    /// server refuses server/discover without it (-32602). That is the whole
    /// point of the method: it is the 2026-07-28 revision's stateless probe,
    /// which is why it carries its own version rather than relying on one
    /// negotiated at initialize.
    /// </remarks>
    internal async Task<JsonElement> DiscoverAsync()
    {
        // The 2026-07-28 revision demands three things in agreement, each
        // refused separately: the body's _meta protocol version, an
        // MCP-Protocol-Version header matching it, and an Mcp-Method header
        // naming the method. They are set per-REQUEST rather than on the
        // session defaults — a stray MCP-Protocol-Version left on this client
        // would change how every later request in the same test is validated.
        // Under 2026-07-28 the per-request _meta carries what initialize used
        // to: the protocol version, the client's capabilities, and its
        // identity. clientInfo living HERE rather than in a session is why
        // ToolSupport must read it per request and never cache it.
        const string Body = """
            {"jsonrpc":"2.0","id":9,"method":"server/discover","params":{"_meta":{
            "io.modelcontextprotocol/protocolVersion":"2026-07-28",
            "io.modelcontextprotocol/clientCapabilities":{},
            "io.modelcontextprotocol/clientInfo":{"name":"knapper-raw-probe","version":"1.0"}}}}
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/")
        {
            Content = new StringContent(Body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", "server/discover");
        using var response = await _http.SendAsync(request);
        using var document = JsonDocument.Parse(Payload(await response.Content.ReadAsStringAsync()));
        if (document.RootElement.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"JSON-RPC error: {error}");
        return document.RootElement.GetProperty("result").Clone();
    }

    /// <summary>The tool objects from a real tools/list response, in wire order.</summary>
    internal async Task<IReadOnlyList<JsonElement>> ListToolsAsync()
    {
        var result = await RequestAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        return [.. result.GetProperty("tools").EnumerateArray()];
    }

    /// <summary>The tools/call result object — structuredContent, content, isError.</summary>
    internal Task<JsonElement> CallToolAsync(string name, object arguments) =>
        RequestAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = 3,
            method = "tools/call",
            @params = new { name, arguments },
        }));

    private async Task<JsonElement> RequestAsync(string body)
    {
        var raw = await PostAsync(body);
        using var document = JsonDocument.Parse(Payload(raw));
        if (document.RootElement.TryGetProperty("error", out var error))
            throw new InvalidOperationException($"JSON-RPC error: {error}");
        // Cloned: the JsonDocument is disposed at the end of this scope, and
        // JsonElements into a disposed document read as garbage rather than
        // throwing.
        return document.RootElement.GetProperty("result").Clone();
    }

    private async Task<string> PostAsync(string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("/", content);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>The transport answers either plain JSON or an SSE frame.</summary>
    private static string Payload(string body) =>
        body.TrimStart().StartsWith('{')
            ? body
            : body.Split('\n').First(l => l.StartsWith("data:", StringComparison.Ordinal))[5..].Trim();
}
