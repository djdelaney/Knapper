using System.Diagnostics;
using Knapper.Core;
using Knapper.Core.Mutation;
using Knapper.Core.Options;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace Knapper.Mcp.Tools;

/// <summary>
/// Shared per-call plumbing: typed-error mapping, caller identity for the
/// audit trail, and tool-call logging. Every tool method body runs through
/// <see cref="Run{T}"/> so a <see cref="KnapperException"/> reaches the
/// client as a structured MCP error whose message LEADS with the error code —
/// the agent-side contract for retry decisions ("PreconditionFailed → re-read,
/// never retry the old base").
/// </summary>
public sealed class ToolSupport(
    IHttpContextAccessor httpContext,
    IOptions<McpOptions> mcpOptions,
    ILogger<ToolSupport> logger)
{
    public T Run<T>(string tool, Func<T> body)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = body();
            Log(tool, "ok", started);
            return result;
        }
        catch (KnapperException e)
        {
            Log(tool, e.Code.ToString(), started);
            throw new McpException($"[{e.Code}] {e.Message}");
        }
        catch (OperationCanceledException)
        {
            Log(tool, "cancelled", started);
            // Cancellation is transport-level, not a VaultErrorCode — but the
            // wire message keeps the same [Code] shape agents parse.
            throw new McpException("[QueryCancelled] the request was cancelled before completion");
        }
    }

    /// <summary>
    /// Caller identity for the audit trail: the Cloudflare Access assertion's
    /// email (Managed OAuth) or common name (service token), else the sub
    /// claim; "loopback" for unauthenticated same-box callers. RequestId is
    /// the ASP.NET trace identifier — greppable across audit + server logs.
    /// </summary>
    public AuditContext Caller()
    {
        var context = httpContext.HttpContext;
        var user = context?.User;
        var client =
            user?.FindFirst("email")?.Value
            ?? user?.FindFirst("common_name")?.Value
            ?? user?.FindFirst("sub")?.Value
            ?? "loopback";
        return new AuditContext(client, context?.TraceIdentifier);
    }

    private void Log(string tool, string outcome, long startedTimestamp)
    {
        if (!mcpOptions.Value.LogToolCalls)
            return;
        logger.LogInformation("tool {Tool} → {Outcome} in {ElapsedMs}ms (client {Client})",
            tool, outcome, Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds.ToString("F0"),
            Caller().Client);
    }
}
