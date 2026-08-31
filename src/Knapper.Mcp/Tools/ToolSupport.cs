using System.Diagnostics;
using Knapper.Core;
using Knapper.Core.Mutation;
using Knapper.Core.Options;
using Knapper.Core.Query;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

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
        logger.LogInformation("tool {Tool} → {Outcome} in {ElapsedMs}ms (client {Client} app {ClientApp})",
            tool, outcome, Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds.ToString("F0"),
            Caller().Client, CallingApp());
    }

    /// <summary>
    /// The calling client application, parked by the tools/call filter that
    /// is the only request-scoped seam with access to it.
    ///
    /// ⛔ Do NOT "simplify" this into an <see cref="McpServer"/> parameter on
    /// the tool methods, which is how the SDK documents this access. That
    /// parameter LEAKS INTO THE PUBLISHED inputSchema: the exclusion is
    /// conditional on <c>IServiceProviderIsService</c> agreeing, and when it
    /// does not, the generator walks McpServer's whole object graph and emits
    /// the permissive `true` for every loosely typed member inside it. A
    /// strict client rejects that manifest WHOLE — all fourteen tools go dark
    /// over a parameter nobody meant to publish, which is the 0.3.2 outage
    /// exactly. Measured here on SDK 2.1.0; pinned from the other side by
    /// ToolManifestTests.No_tool_advertises_the_request_scoped_server_as_an_argument.
    /// The other tempting shortcut, resolving McpServer from
    /// <c>HttpContext.RequestServices</c>, is not registered there and yields
    /// "unknown" on every line — which is indistinguishable from a fleet of
    /// clients that decline to identify themselves.
    /// </summary>
    private static string CallingApp() =>
        // A FOURTH state, distinct from the three ClientApp can report: the
        // filter never ran at all, so it was never registered. Folded in with
        // the others it would look like a client problem.
        CallingClient.Name ?? "unfiltered";

    /// <summary>
    /// The client APPLICATION's self-reported name, for the call-economics
    /// report (<c>ops/call-economics.sh</c>). It answers a question
    /// <see cref="Caller"/> structurally cannot: Access identity is per-USER,
    /// so Cowork, Desktop, mobile, claude.ai and Claude Code all collapse into
    /// one email — while the round-trip cost that report measures is a
    /// property of the SURFACE, not the person (a directly-configured client
    /// measured ~120ms against the relay's ~3s). Without this, a window whose
    /// surface mix shifted is indistinguishable from one whose agents changed
    /// behaviour, which is the confound the whole report exists to avoid.
    ///
    /// Read from the REQUEST-SCOPED server the SDK binds to a tool method's
    /// <see cref="McpServer"/> parameter — never from a captured or singleton
    /// one. On protocol revision 2026-07-28 and later clientInfo is carried
    /// per-request in <c>_meta</c> rather than fixed at <c>initialize</c>, so
    /// a cached read reports whichever client happened to open the session
    /// first, for every client after it, in the green.
    /// </summary>
    internal static string ClientApp(McpServer? server)
    {
        if (server is null)
            return "no-server";
        if (ClientApp(server.ClientInfo) is { } named)
            return named;
        // No name — and WHY is the whole question. ClientCapabilities is
        // populated by the initialize handshake and is null in stateless
        // transport mode, so it separates "this client established a session
        // and chose not to name itself" from "no completed initialize ever
        // reached this server instance". Collapsed into one bucket (as this
        // shipped in 0.6.1) the two are indistinguishable, and the second is
        // the interesting one: it would mean per-call session setup, which is
        // the same layer as the server/discover round trip that already
        // doubles traversals per tool call (docs/call-economics.md).
        return server.ClientCapabilities is null ? "no-session" : "no-client-info";
    }

    /// <summary>
    /// The sanitised client name, or null when the client supplied none —
    /// null rather than a placeholder so the caller above can say WHY.
    /// </summary>
    internal static string? ClientApp(ModelContextProtocol.Protocol.Implementation? clientInfo)
    {
        var name = clientInfo?.Name;
        if (string.IsNullOrWhiteSpace(name))
            return null;

        // Client-CONTROLLED text on its way into an operator-facing report
        // that is written to be pasted. Bounded and stripped of everything
        // but printable ASCII: a name carrying a newline or a tab would
        // otherwise forge rows in a tab-separated report whose columns are
        // tool names and call counts.
        var clean = new string([.. name
            .Where(c => c is >= ' ' and <= '~')
            .Take(MaxClientAppLength)]);
        return clean.Length == 0 ? "unnamed" : clean;
    }

    private const int MaxClientAppLength = 64;
}
