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
    private static ToolSupport NewSupport() => new(
        new HttpContextAccessor(),
        Options.Create(new McpOptions { LogToolCalls = false }),
        NullLogger<ToolSupport>.Instance);

    [Fact]
    public void Knapper_exceptions_lead_with_their_code()
    {
        var ex = Should.Throw<McpException>(() => NewSupport().Run<int>("t",
            () => throw new KnapperException(VaultErrorCode.PreconditionFailed, "stale")));
        ex.Message.ShouldStartWith("[PreconditionFailed]");
    }

    [Fact]
    public void Raw_filesystem_exceptions_become_IoError()
    {
        var ex = Should.Throw<McpException>(() => NewSupport().Run<int>("t",
            () => throw new IOException("disk full")));
        ex.Message.ShouldStartWith("[IoError]");

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
}
