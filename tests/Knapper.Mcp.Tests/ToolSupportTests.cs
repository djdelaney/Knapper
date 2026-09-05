using Knapper.Core;
using Knapper.Core.Options;
using Knapper.Mcp.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace Knapper.Mcp.Tests;

/// <summary>
/// Every MCP-visible failure leads with a stable bracketed code — agents
/// parse that prefix to decide "re-read and rebuild" vs "give up". Raw
/// filesystem exceptions from the query layer and outright bugs must not
/// reach the wire shapeless.
/// </summary>
public class ToolSupportTests
{
    private static ToolSupport NewSupport(KnapperMetrics? metrics = null) => new(
        new HttpContextAccessor(),
        Options.Create(new McpOptions { LogToolCalls = false }),
        NullLogger<ToolSupport>.Instance,
        metrics ?? new KnapperMetrics());

    [Fact]
    public void Knapper_exceptions_lead_with_their_code()
    {
        var ex = Should.Throw<McpException>(() => NewSupport().Run<int>("t",
            () => throw new KnapperException(VaultErrorCode.PreconditionFailed, "stale")));
        ex.Message.ShouldStartWith("[PreconditionFailed]");
    }

    [Fact]
    public void Raw_filesystem_exceptions_become_IoError_without_leaking_os_detail()
    {
        // OS messages embed absolute paths — wire text is static, detail
        // stays in the server log (same pattern as [Internal]).
        var ex = Should.Throw<McpException>(() => NewSupport().Run<int>("t",
            () => throw new IOException("disk full at /var/lib/secret-place")));
        ex.Message.ShouldStartWith("[IoError]");
        ex.Message.ShouldNotContain("/var/lib/secret-place");

        var ex2 = Should.Throw<McpException>(() => NewSupport().Run<int>("t",
            () => throw new UnauthorizedAccessException("denied")));
        ex2.Message.ShouldStartWith("[IoError]");
    }

    [Fact]
    public void Unexpected_exceptions_become_Internal_without_leaking_details()
    {
        var ex = Should.Throw<McpException>(() => NewSupport().Run<int>("t",
            () => throw new InvalidOperationException("secret internals")));
        ex.Message.ShouldStartWith("[Internal]");
        ex.Message.ShouldNotContain("secret internals");
    }

    [Fact]
    public void Cancellation_keeps_the_bracketed_shape()
    {
        var ex = Should.Throw<McpException>(() => NewSupport().Run<int>("t",
            () => throw new OperationCanceledException()));
        ex.Message.ShouldStartWith("[QueryCancelled]");
    }

    [Fact]
    public void Every_outcome_feeds_the_metrics_surface()
    {
        // The monitor's rate signals (brief §8) hang off these counters —
        // and they must record regardless of the LogToolCalls toggle
        // (NewSupport runs with logging off).
        using var metrics = new KnapperMetrics();
        var support = NewSupport(metrics);

        support.Run("t", () => 1); // plain ok
        Should.Throw<McpException>(() => support.Run<int>("t",
            () => throw new KnapperException(VaultErrorCode.QueryTimeout, "slow")));
        Should.Throw<McpException>(() => support.Run<int>("t",
            () => throw new KnapperException(VaultErrorCode.PreconditionFailed, "stale")));
        Should.Throw<McpException>(() => support.Run<int>("t",
            () => throw new IOException("disk")));
        // A truncated, generation-changed envelope on a successful call.
        support.Run("t", () => new Knapper.Core.Query.QueryEnvelope<int>(
            [1], Truncated: true, null, 1, 1, null, 1, 2, ChangedDuringQuery: true, []));

        var snapshot = metrics.Read();
        snapshot.ToolCalls.ShouldBe(5);
        snapshot.ToolErrors.ShouldBe(3);
        snapshot.QueryTimeouts.ShouldBe(1);
        snapshot.StaleRejections.ShouldBe(1);
        snapshot.IoErrors.ShouldBe(1);
        snapshot.TruncatedResponses.ShouldBe(1);
        snapshot.GenerationChangedResponses.ShouldBe(1);
    }
}
