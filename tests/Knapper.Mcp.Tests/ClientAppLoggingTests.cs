using System.Collections.Concurrent;
using Knapper.Mcp.Tools;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;

namespace Knapper.Mcp.Tests;

/// <summary>
/// The client APPLICATION name on every tool-call log line — the axis
/// <c>ops/call-economics.sh</c> cannot otherwise see. Cloudflare Access
/// identity is per-USER, so every surface (Cowork, Desktop, mobile,
/// claude.ai, Claude Code) collapses into one email, while the round-trip
/// cost that report measures is a property of the SURFACE.
///
/// Three things can break this silently, and each has a test here:
/// the SDK could stop binding the <see cref="McpServer"/> parameter (every
/// line reads "unknown", which looks exactly like clients that decline to
/// identify themselves); the name could arrive unsanitised into a report
/// written to be pasted; or someone could "simplify" the lookup into the
/// McpServer tool PARAMETER the SDK documents, which leaks into the
/// published inputSchema and takes the whole tool list down with it (see
/// ToolSupport.CallingApp, and the manifest test named there).
/// </summary>
public class ClientAppLoggingTests
{
    [Fact]
    public async Task A_tool_call_is_logged_with_the_calling_client_applications_name()
    {
        var capture = new CapturingLoggerProvider();
        using var factory = new KnapperMcpFactory(null);
        using var configured = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton<ILoggerProvider>(capture)));
        using var http = configured.CreateClient();

        // The SDK client, not RawMcp: attribution is session state
        // established at initialize, and RawMcp deliberately carries no
        // Mcp-Session-Id, so every one of its requests is a fresh
        // uninitialized session that legitimately has no client info. This
        // test needs a client that holds a session the way a real one does.
        await using var client = await McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/") }, http),
            new McpClientOptions
            {
                ClientInfo = new ModelContextProtocol.Protocol.Implementation
                {
                    Name = "knapper-test-client",
                    Version = "1.0",
                },
            });
        await client.CallToolAsync("vault_read", new Dictionary<string, object?> { ["path"] = "Notes/Daily.md" });

        var call = capture.Entries.ShouldHaveSingleItem();
        call["Tool"].ShouldBe("vault_read");
        // The KEY is the contract, not just the value: ops/call-economics.sh
        // reads $m.State.ClientApp out of the journal's structured payload,
        // so a renamed placeholder breaks the report and nothing else.
        call["ClientApp"].ShouldBe("knapper-test-client");
    }

    [Theory]
    // Client-CONTROLLED text on its way into a report written to be pasted.
    [InlineData("claude-ai", "claude-ai")]
    // null, not a placeholder: the caller decides WHY there is no name, and
    // the three reasons need to stay tellable apart in the report.
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    // A tab or a newline would forge rows in a tab-separated report whose
    // columns are tool names and call counts.
    [InlineData("evil\tname\ncalls 999999", "evilnamecalls 999999")]
    // Non-ASCII is stripped rather than passed through: the report is read in
    // a terminal and pasted into issues.
    [InlineData("клиент", "unnamed")]
    public void A_client_name_is_bounded_and_stripped_before_it_reaches_the_log(string? name, string? expected) =>
        ToolSupport.ClientApp(Reporting(name)).ShouldBe(expected);

    [Fact]
    public void An_overlong_client_name_cannot_flood_the_log() =>
        ToolSupport.ClientApp(Reporting(new string('x', 5000)))!.Length.ShouldBe(64);

    [Fact]
    public void A_missing_server_is_named_as_such_rather_than_blamed_on_the_client() =>
        ToolSupport.ClientApp((McpServer?)null).ShouldBe("no-server");

    [Fact]
    public async Task A_caller_with_no_established_session_is_reported_as_no_session()
    {
        // RawMcp deliberately carries no Mcp-Session-Id, so every request is
        // its own uninitialised session — no ClientInfo AND no
        // ClientCapabilities. This is not a contrived state: it is exactly
        // what CT 106 reports for every call arriving through the claude.ai
        // relay, and this test is where that is reproducible locally.
        var capture = new CapturingLoggerProvider();
        using var factory = new KnapperMcpFactory(null);
        using var configured = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.AddSingleton<ILoggerProvider>(capture)));

        var session = await RawMcp.OpenAsync(configured.CreateClient());
        await session.CallToolAsync("vault_read", new { path = "Notes/Daily.md" });

        // NOT "unknown": that bucket could not tell this apart from a client
        // that has a session and declines to name itself, which is the
        // distinction the production diagnosis turns on.
        capture.Entries.ShouldHaveSingleItem()["ClientApp"].ShouldBe("no-session");
    }

    private static ModelContextProtocol.Protocol.Implementation? Reporting(string? name) =>
        name is null ? null : new ModelContextProtocol.Protocol.Implementation { Name = name, Version = "1" };
}

/// <summary>Captures ToolSupport's structured log state, which is what the report parses.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<IReadOnlyDictionary<string, string?>> _entries = new();

    internal IReadOnlyList<IReadOnlyDictionary<string, string?>> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) =>
        categoryName == typeof(ToolSupport).FullName
            ? new CapturingLogger(_entries)
            : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public void Dispose() { }

    private sealed class CapturingLogger(ConcurrentQueue<IReadOnlyDictionary<string, string?>> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
                sink.Enqueue(pairs.ToDictionary(p => p.Key, p => p.Value?.ToString()));
        }
    }
}
