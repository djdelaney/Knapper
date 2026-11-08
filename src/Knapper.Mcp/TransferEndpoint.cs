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
        AuditLog audit,
        UploadGrantStore uploads,
        IOptions<UploadOptions> upload)
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
            if (!upload.Value.Enabled)
            {
                // Refused before reading a byte, so Kestrel never sends a 100.
                await Refuse(context, refusals, 404, VaultErrorCode.UploadDisabled,
                    "uploads are turned off on this deployment").ConfigureAwait(false);
                return;
            }
            await ReceiveUpload(context, uploads, refusals, audit, id).ConfigureAwait(false);
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

    internal sealed record UploadStaged(string UploadId, string State, long ByteLength, string Sha256);

    /// <summary>
    /// The upload PUT (§4.2). Authenticate and claim BEFORE reading a byte —
    /// the body is buffered only into the exact-size array the grant reserved
    /// at prepare — then enforce the bound length on the STREAM (a matching
    /// prefix of a longer body is never staged) and the bound sha256. The
    /// payload is opaque: no decoding, decompression or MIME handling. Success
    /// means STAGED, never written: the vault write happens at commit, through
    /// the authenticated MCP surface. Every admitted PUT spends the grant,
    /// including one refused for its headers; a 429 for capacity claims
    /// nothing, and an invalid token never burns a valid grant.
    /// </summary>
    private static async Task ReceiveUpload(
        HttpContext context, UploadGrantStore uploads, TransferRefusalLog refusals, AuditLog audit, string id)
    {
        var token = ReadToken(context.Request.Headers.Authorization.ToString(), UploadScheme);
        var (status, grant, remaining) = uploads.BeginReceive(id, token);
        switch (status)
        {
            case UploadGrantStore.ReceiveStatus.InvalidToken:
                context.Response.Headers.WWWAuthenticate = UploadScheme;
                await Refuse(context, refusals, 401, VaultErrorCode.UploadInvalidToken,
                    "missing, malformed or wrong upload token").ConfigureAwait(false);
                return;
            case UploadGrantStore.ReceiveStatus.Expired:
                AuditUpload(audit, context, grant!, nameof(VaultErrorCode.UploadExpired));
                await Refuse(context, refusals, 410, VaultErrorCode.UploadExpired,
                    "this upload grant expired — prepare again").ConfigureAwait(false);
                return;
            case UploadGrantStore.ReceiveStatus.AlreadyClaimed:
                AuditUpload(audit, context, grant!, nameof(VaultErrorCode.UploadAlreadyClaimed));
                await Refuse(context, refusals, 409, VaultErrorCode.UploadAlreadyClaimed,
                    "this upload grant already took its one transfer — call vault_upload_status").ConfigureAwait(false);
                return;
            case UploadGrantStore.ReceiveStatus.Busy:
                await Refuse(context, refusals, 429, VaultErrorCode.UploadCapacityExceeded,
                    "too many uploads are arriving right now; nothing was claimed — retry the same grant shortly").ConfigureAwait(false);
                return;
        }

        // From admission on, the handler holds the grant's receiver lease:
        // every exit must end it exactly once (CompleteReceive or
        // FailReceive), or the slot and the reserved bytes would stay counted
        // forever. The finally below is what makes that unconditional.
        var settled = false;
        try
        {
            await ReceiveAdmitted(context, uploads, refusals, audit, id, grant!, remaining, () => settled = true).ConfigureAwait(false);
        }
        finally
        {
            if (!settled)
                uploads.FailReceive(id, VaultErrorCode.UploadTransferFailed, "the transfer ended without a result");
        }
    }

    private static async Task ReceiveAdmitted(
        HttpContext context, UploadGrantStore uploads, TransferRefusalLog refusals, AuditLog audit, string id,
        UploadGrantStore.View grant, TimeSpan remaining, Action settle)
    {
        var binding = grant.Binding;
        var request = context.Request;
        async Task Fail(int statusCode, VaultErrorCode code, string message)
        {
            uploads.FailReceive(id, code, message);
            settle();
            AuditUpload(audit, context, grant, code.ToString());
            await Refuse(context, refusals, statusCode, code, message).ConfigureAwait(false);
        }

        if (!string.Equals(request.ContentType?.Split(';')[0].Trim(), "application/octet-stream", StringComparison.OrdinalIgnoreCase)
            || (request.Headers.ContentEncoding.Count > 0
                && !string.Equals(request.Headers.ContentEncoding.ToString().Trim(), "identity", StringComparison.OrdinalIgnoreCase)))
        {
            await Fail(415, VaultErrorCode.InvalidArgument,
                "uploads take Content-Type: application/octet-stream and no Content-Encoding — the grant is spent; prepare again").ConfigureAwait(false);
            return;
        }
        if (request.ContentLength is { } declared && declared != binding.ByteLength)
        {
            await Fail(declared > binding.ByteLength ? 413 : 400, VaultErrorCode.UploadSizeMismatch,
                $"Content-Length {declared} is not the bound {binding.ByteLength} bytes — the file changed? re-hash and prepare again").ConfigureAwait(false);
            return;
        }

        byte[] buffer;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted))
        {
            deadline.CancelAfter(remaining);
            try
            {
                buffer = new byte[binding.ByteLength];
                var filled = 0;
                while (filled < buffer.Length)
                {
                    var read = await request.Body.ReadAsync(buffer.AsMemory(filled), deadline.Token).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    filled += read;
                }
                if (filled < buffer.Length)
                {
                    await Fail(400, VaultErrorCode.UploadSizeMismatch,
                        $"the body ended after {filled} of the bound {binding.ByteLength} bytes — re-hash and prepare again").ConfigureAwait(false);
                    return;
                }
                var probe = new byte[1];
                if (await request.Body.ReadAsync(probe, deadline.Token).ConfigureAwait(false) > 0)
                {
                    await Fail(413, VaultErrorCode.UploadSizeMismatch,
                        $"the body is longer than the bound {binding.ByteLength} bytes — re-hash and prepare again").ConfigureAwait(false);
                    return;
                }
            }
            catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
            {
                await Fail(408, VaultErrorCode.UploadTransferTimeout,
                    "the body did not arrive within the receive deadline — prepare again").ConfigureAwait(false);
                return;
            }
            catch (Microsoft.AspNetCore.Http.BadHttpRequestException) when (!context.RequestAborted.IsCancellationRequested)
            {
                // A MALFORMED body (a bad chunk-size line, a framing error) on
                // a connection that is still usable: answer it. Returning
                // silently here let Kestrel send its default 200 for a grant
                // that had just failed (review 2026-11-08).
                await Fail(400, VaultErrorCode.UploadTransferFailed,
                    "the request body is malformed — the grant is spent; prepare again").ConfigureAwait(false);
                return;
            }
            catch (Exception e) when (e is OperationCanceledException or IOException
                or Microsoft.AspNetCore.Http.BadHttpRequestException)
            {
                // The client went away mid-body: nobody is left to answer.
                // Still never a 200, should anything reach the wire.
                uploads.FailReceive(id, VaultErrorCode.UploadTransferFailed, "the connection failed mid-body");
                settle();
                AuditUpload(audit, context, grant, nameof(VaultErrorCode.UploadTransferFailed));
                if (!context.Response.HasStarted)
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
        }

        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(buffer));
        if (!string.Equals(sha, binding.SourceSha256, StringComparison.Ordinal))
        {
            await Fail(400, VaultErrorCode.UploadHashMismatch,
                "the body's sha256 is not the bound one — the local file changed: re-hash and prepare again").ConfigureAwait(false);
            return;
        }
        var staged = uploads.CompleteReceive(id, buffer);
        settle();
        if (!staged)
        {
            // The receive deadline was swept first; the grant is already failed.
            await Refuse(context, refusals, 408, VaultErrorCode.UploadTransferTimeout,
                "the body did not arrive within the receive deadline — prepare again").ConfigureAwait(false);
            return;
        }
        AuditUpload(audit, context, grant, "ready");
        context.Response.StatusCode = StatusCodes.Status200OK;
        // Staged — never the mutation receipt's `Verified`, never Sync delivery.
        await context.Response.WriteAsJsonAsync(
            new UploadStaged(id, UploadService.Wire(UploadState.Ready), binding.ByteLength, sha)).ConfigureAwait(false);
    }

    private static void AuditUpload(AuditLog audit, HttpContext context, UploadGrantStore.View grant, string outcome)
    {
        try
        {
            audit.Append(new AuditLog.Entry(
                DateTimeOffset.UtcNow, "upload_transfer", grant.Binding.Path, outcome,
                RequestId: context.TraceIdentifier,
                AfterSha256: grant.Binding.SourceSha256,
                GrantId: grant.UploadId,
                Principal: grant.Principal.ForAudit(),
                ByteLength: grant.Binding.ByteLength));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
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
