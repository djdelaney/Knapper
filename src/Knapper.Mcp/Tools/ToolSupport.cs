using System.Diagnostics;
using Knapper.Core;
using Knapper.Core.Mutation;
using Knapper.Core.Options;
using Knapper.Core.Query;
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
    ILogger<ToolSupport> logger,
    KnapperMetrics metrics)
{
    public T Run<T>(string tool, Func<T> body)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = body();
            // Metrics are never gated on LogToolCalls — the monitor's rate
            // signals (brief §8) must not vanish with a logging toggle.
            if (result is IFreshnessSignals signals)
                metrics.RecordCompleteness(signals.WasTruncated, signals.MovedDuringQuery);
            metrics.RecordToolOutcome("ok");
            Log(tool, "ok", started);
            return result;
        }
        catch (KnapperException e)
        {
            metrics.RecordToolOutcome(e.Code.ToString());
            Log(tool, e.Code.ToString(), started);
            throw new McpException($"[{e.Code}] {e.Message}");
        }
        catch (OperationCanceledException)
        {
            metrics.RecordToolOutcome("cancelled");
            Log(tool, "cancelled", started);
            // Cancellation is transport-level, not a VaultErrorCode — but the
            // wire message keeps the same [Code] shape agents parse.
            throw new McpException("[QueryCancelled] the request was cancelled before completion");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Query-layer filesystem failures don't pass through Core's
            // mutation-boundary normalization — map them here so every
            // MCP-visible failure leads with a stable bracketed code. Static
            // wire text, detail server-side (mirroring [Internal]): raw OS
            // messages embed ABSOLUTE paths, operational metadata the wire
            // otherwise never discloses.
            metrics.RecordToolOutcome(VaultErrorCode.IoError.ToString());
            Log(tool, VaultErrorCode.IoError.ToString(), started);
            logger.LogError(e, "tool {Tool} filesystem failure", tool);
            throw new McpException(
                $"[{VaultErrorCode.IoError}] filesystem failure — transient or environmental; details in the server log");
        }
        catch (Exception e) when (e is not McpException)
        {
            // A bug, not an environment failure — clients still get the
            // stable [Code] shape; the details go to the server log only.
            metrics.RecordToolOutcome("internal");
            Log(tool, "internal", started);
            logger.LogError(e, "tool {Tool} failed unexpectedly", tool);
            throw new McpException("[Internal] unexpected server error — see server logs");
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
