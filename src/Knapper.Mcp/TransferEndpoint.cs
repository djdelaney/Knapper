using Knapper.Core;
using Knapper.Core.Mutation;
using Knapper.Core.Options;
using Knapper.Core.Transfer;
using Microsoft.Extensions.Options;

namespace Knapper.Mcp;

/// <summary>
/// The whole <c>/transfer</c> prefix (docs/proposals/transfer-grants.md §5.2,
/// §9, §14). The edge's path-scoped Bypass sends EVERYTHING under the prefix
/// here unauthenticated, so this one handler owns all of it: the two grant
/// routes, and a 404 for every other path (the bare prefix, an encoded-slash
/// escape like <c>/transfer/..%2Fmcp</c>, an unknown subpath). No other
/// endpoint may ever be mapped under the prefix.
///
/// No Access policy applies here — the grant token and HostGuard are all
/// there is, by design — and the download route serves ONLY the in-memory
/// snapshot a grant holds: it opens no file and resolves no path.
/// </summary>
internal static class TransferEndpoint
{
    internal const string DownloadScheme = "KnapperDownload";
    internal const string UploadScheme = "KnapperUpload";

    internal sealed record TransferError(string Code, string Message);

    internal static async Task Handle(
        HttpContext context,
        DownloadGrantStore store,
        IOptions<TransferOptions> transfer,
        IOptions<DownloadOptions> download,
        TransferRefusalLog refusals,
        AuditLog audit)
    {
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";

        if (transfer.Value.LocalOnly && !HostGuard.IsLocalRequest(context))
        {
            await Refuse(context, refusals, 404, VaultErrorCode.NotFound, "no such transfer route").ConfigureAwait(false);
            return;
        }

        // The raw path: Kestrel leaves %2F encoded, so an encoded-slash escape
        // arrives as one opaque segment and matches nothing below.
        var tail = (context.Request.Path.Value ?? "")[TransferOptions.PathPrefix.Length..];
        var parts = tail.Split('/');
        if (parts.Length != 3 || parts[0].Length != 0 || parts[2].Length == 0)
        {
            await Refuse(context, refusals, 404, VaultErrorCode.NotFound, "no such transfer route").ConfigureAwait(false);
            return;
        }

        var kind = parts[1];
        var id = parts[2];
        var method = context.Request.Method;

        if (kind.Equals("downloads", StringComparison.OrdinalIgnoreCase))
        {
            if (!HttpMethods.IsGet(method))
            {
                response.Headers.Allow = "GET";
                await Refuse(context, refusals, 405, VaultErrorCode.InvalidArgument, "downloads accept GET only").ConfigureAwait(false);
                return;
            }
            if (!download.Value.Enabled)
            {
                await Refuse(context, refusals, 404, VaultErrorCode.DownloadDisabled,
                    "downloads are turned off on this deployment").ConfigureAwait(false);
                return;
            }
            await ServeDownload(context, store, refusals, audit, id).ConfigureAwait(false);
            return;
        }

        if (kind.Equals("uploads", StringComparison.OrdinalIgnoreCase))
        {
            if (!HttpMethods.IsPut(method))
            {
                response.Headers.Allow = "PUT";
                await Refuse(context, refusals, 405, VaultErrorCode.InvalidArgument, "uploads accept PUT only").ConfigureAwait(false);
                return;
            }
            // Not built in this release: refuse before reading a byte of the
            // body (so Kestrel never sends 100 Continue for it).
            await Refuse(context, refusals, 404, VaultErrorCode.UploadDisabled,
                "uploads are not available on this server").ConfigureAwait(false);
            return;
        }

        await Refuse(context, refusals, 404, VaultErrorCode.NotFound, "no such transfer route").ConfigureAwait(false);
    }

    private static async Task ServeDownload(
        HttpContext context, DownloadGrantStore store, TransferRefusalLog refusals, AuditLog audit, string id)
    {
        var token = ReadToken(context.Request.Headers.Authorization.ToString(), DownloadScheme);
        var claim = store.TryClaim(id, token);
        switch (claim.Status)
        {
            case DownloadGrantStore.ClaimStatus.InvalidToken:
                // No audit: an invalid token proves nothing about any grant,
                // and an fsynced record per internet probe is the cost §11
                // refuses to pay. The bounded refusal log covers it.
                context.Response.Headers.WWWAuthenticate = DownloadScheme;
                await Refuse(context, refusals, 401, VaultErrorCode.DownloadInvalidToken,
                    "missing, malformed or wrong download token").ConfigureAwait(false);
                return;
            case DownloadGrantStore.ClaimStatus.Expired:
                AuditOutcome(audit, context, claim, nameof(VaultErrorCode.DownloadExpired));
                await Refuse(context, refusals, 410, VaultErrorCode.DownloadExpired,
                    "this download grant expired — prepare again").ConfigureAwait(false);
                return;
            case DownloadGrantStore.ClaimStatus.AlreadyClaimed:
                AuditOutcome(audit, context, claim, nameof(VaultErrorCode.DownloadAlreadyClaimed));
                await Refuse(context, refusals, 409, VaultErrorCode.DownloadAlreadyClaimed,
                    "this download grant was already used — prepare again").ConfigureAwait(false);
                return;
        }

        var outcome = "aborted";
        try
        {
            // Always the full snapshot with 200: Range and conditional headers
            // are ignored, and no ETag or Last-Modified is ever sent — a 206 or
            // 304 would fail the client's hash check with a misleading error.
            var bytes = claim.Bytes!;
            var response = context.Response;
            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = "application/octet-stream";
            response.ContentLength = bytes.LongLength;
            response.Headers.ContentDisposition = "attachment";
            await response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
            await response.CompleteAsync().ConfigureAwait(false);
            // "sent", never "received": the server can only say every byte was
            // handed to the transport. The client's hash check is the one
            // proof the bytes arrived intact (§6).
            outcome = context.RequestAborted.IsCancellationRequested ? "aborted" : "sent";
        }
        catch (Exception e) when (e is OperationCanceledException or IOException)
        {
            // The client went away mid-body. The grant is spent either way;
            // the record is what tells a completed export from a dropped one.
        }
        finally
        {
            claim.Release();
            AuditOutcome(audit, context, claim, outcome);
        }
    }

    /// <summary>
    /// Best-effort transfer-outcome record (§11), linked to the prepare record
    /// by grant ID and principal. It can never block or fail a response — the
    /// prepare record is the one that must land — and carries no content and
    /// no token.
    /// </summary>
    private static void AuditOutcome(AuditLog audit, HttpContext context, DownloadGrantStore.Claim claim, string outcome)
    {
        try
        {
            audit.Append(new AuditLog.Entry(
                DateTimeOffset.UtcNow, "download_transfer", claim.Path ?? "", outcome,
                RequestId: context.TraceIdentifier,
                AfterSha256: claim.Sha256,
                GrantId: claim.DownloadId,
                Principal: claim.Principal?.ForAudit(),
                ByteLength: claim.Bytes?.LongLength));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
        }
    }

    /// <summary>The token from <c>Authorization: &lt;scheme&gt; &lt;token&gt;</c>, or null. The scheme name is case-insensitive (RFC 9110).</summary>
    internal static string? ReadToken(string? header, string scheme)
    {
        if (string.IsNullOrEmpty(header))
            return null;
        var space = header.IndexOf(' ');
        if (space <= 0 || !header.AsSpan(0, space).Equals(scheme, StringComparison.OrdinalIgnoreCase))
            return null;
        var token = header[(space + 1)..].Trim();
        return token.Length == 0 ? null : token;
    }

    private static Task Refuse(HttpContext context, TransferRefusalLog refusals, int status, VaultErrorCode code, string message)
    {
        refusals.Record(status, code);
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new TransferError(code.ToString(), message));
    }
}

/// <summary>
/// Bounded logging for refused transfer requests (§11, decision 5). Every
/// request comes from cloudflared's loopback peer, so nothing here can key on
/// a client; and it never stands in front of authentication — a valid token
/// is checked first and never throttled. This bounds only what a refusal
/// COSTS: at most <see cref="PerMinute"/> log lines a minute, then one
/// summary of how many were suppressed. No fsynced audit record per probe.
/// </summary>
public sealed class TransferRefusalLog(ILogger<TransferRefusalLog> logger, TimeProvider? time = null)
{
    internal const int PerMinute = 20;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private DateTimeOffset _windowStart = DateTimeOffset.MinValue;
    private int _logged;
    private int _suppressed;

    internal void Record(int status, VaultErrorCode code)
    {
        int suppressedBefore;
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (now - _windowStart >= TimeSpan.FromMinutes(1))
            {
                suppressedBefore = _suppressed;
                _windowStart = now;
                _logged = 0;
                _suppressed = 0;
            }
            else
            {
                suppressedBefore = 0;
            }
            if (_logged >= PerMinute)
            {
                _suppressed++;
                return;
            }
            _logged++;
        }
        if (suppressedBefore > 0)
            logger.LogWarning("transfer refusals: {Suppressed} more were not logged in the previous minute", suppressedBefore);
        logger.LogInformation("transfer refused {Status} {Code}", status, code.ToString());
    }
}
