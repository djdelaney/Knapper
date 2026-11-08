using Knapper.Core.Mutation;
using Knapper.Core.Options;

namespace Knapper.Core.Transfer;

public sealed record DownloadPrepareRequest(string Path, string? ExpectSha256);

/// <summary>One grant (docs/proposals/transfer-grants.md §14). <c>Fetch</c> is fixed text with no token in it.</summary>
public sealed record DownloadGrant(
    string DownloadId,
    string DownloadUrl,
    string DownloadToken,
    DateTimeOffset ExpiresAt,
    string Path,
    string Sha256,
    long ByteLength,
    string Fetch);

/// <summary>Per-item result: a grant, or a typed error — one bad item never hides the rest.</summary>
public sealed record DownloadPrepareItem(string Path, DownloadGrant? Grant, string? ErrorCode, string? ErrorMessage);

public sealed record DownloadPrepareResult(IReadOnlyList<DownloadPrepareItem> Items);

/// <summary>
/// <c>vault_download_prepare</c>'s body (§5.1): snapshot each file, issue a
/// grant over the snapshot, and durably audit it BEFORE returning it — the
/// first audited read in Knapper, because a download carries vault bytes past
/// the host boundary into a workspace where outbound tools can reach them.
/// </summary>
public sealed class DownloadService
{
    public const int MaxItems = 16;
    private const string Op = "download_prepare";

    private readonly DownloadSnapshotReader _reader;
    private readonly DownloadGrantStore _store;
    private readonly AuditLog _audit;
    private readonly DownloadOptions _download;
    private readonly string _urlBase;
    private readonly string _fetch;

    public DownloadService(
        DownloadSnapshotReader reader, DownloadGrantStore store, AuditLog audit,
        TransferOptions transfer, DownloadOptions download)
    {
        _reader = reader;
        _store = store;
        _audit = audit;
        _download = download;
        _urlBase = transfer.PublicOrigin.TrimEnd('/') + TransferOptions.PathPrefix + "/downloads/";
        _fetch = FetchHint(transfer.PublicOrigin.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "http" : "https");
    }

    /// <summary>
    /// The recipe, as fixed text: the agent sets TOKEN, URL, SHA, LEN and
    /// DEST itself, so no secret is ever interpolated here. Its prose lines
    /// are shell comments, so the WHOLE string runs as written — the
    /// acceptance suite runs it literally with real bash, curl and sha256sum
    /// (§6); change it and that test is the judge.
    /// </summary>
    internal static string FetchHint(string proto) =>
        "# Set TOKEN=<downloadToken> URL=<downloadUrl> SHA=<sha256> LEN=<byteLength> DEST=<local path>, then run\n" +
        "# this whole text in bash or zsh. The token goes to curl on stdin, never on the command line.\n" +
        "# The grant works ONCE: any failure means prepare again. Never use a file whose hash did not match.\n" +
        "( umask 077; tmp=$(mktemp \"$(dirname \"$DEST\")/.knapper-dl.XXXXXX\") &&\n" +
        "  printf 'Authorization: KnapperDownload %s\\n' \"$TOKEN\" |\n" +
        $"  curl --fail -sS --proto '={proto}' --max-redirs 0 --max-time 120 \\\n" +
        "       --max-filesize \"$LEN\" -H @- -o \"$tmp\" \"$URL\" &&\n" +
        "  echo \"$SHA  $tmp\" | sha256sum -c - &&\n" +
        "  mv \"$tmp\" \"$DEST\" || { rm -f \"$tmp\"; false; } )\n";

    public DownloadPrepareResult Prepare(
        TransferPrincipal? principal, IReadOnlyList<DownloadPrepareRequest>? items, AuditContext caller)
    {
        if (!_download.Enabled)
        {
            throw new KnapperException(VaultErrorCode.DownloadDisabled,
                "downloads are turned off on this deployment (Download:Enabled)");
        }
        if (principal is null)
        {
            throw new KnapperException(VaultErrorCode.DownloadNotPermitted,
                "this request carries no verified principal to bind a grant to — downloads need an authenticated " +
                "Access identity (or Transfer:LocalOnly on a same-machine deployment)");
        }
        if (items is null || items.Count is < 1 or > MaxItems)
        {
            throw new KnapperException(VaultErrorCode.InvalidArgument,
                $"items must hold 1–{MaxItems} entries, got {items?.Count ?? 0}");
        }
        // Every grant this call issues is tracked until the response exists:
        // if anything unanticipated escapes a later item, the earlier grants
        // were never handed to anyone, so they are withdrawn rather than left
        // holding budget behind a response that never arrived.
        var issuedHere = new List<string>();
        try
        {
            var results = new List<DownloadPrepareItem>(items.Count);
            foreach (var item in items)
            {
                var result = PrepareOne(principal, item, caller);
                if (result.Grant is { } grant)
                    issuedHere.Add(grant.DownloadId);
                results.Add(result);
            }
            return new DownloadPrepareResult(results);
        }
        catch
        {
            foreach (var id in issuedHere)
                _store.Revoke(id);
            throw;
        }
    }

    private DownloadPrepareItem PrepareOne(TransferPrincipal principal, DownloadPrepareRequest item, AuditContext caller)
    {
        string? relative = null;
        var auditFailed = false;
        try
        {
            relative = _reader.Relative(item.Path);
            // Capacity is reserved inside the read, BEFORE the file's bytes
            // are allocated; a full budget refuses with nothing read.
            var snapshot = _reader.Read(item.Path, item.ExpectSha256, size => _store.Reserve(principal, size));
            DownloadGrantStore.Issued issued;
            using (var reservation = snapshot.Reservation!)
                issued = _store.Issue(reservation, snapshot.Bytes, snapshot.Path, snapshot.Sha256);
            try
            {
                _audit.Append(Entry(snapshot.Path, "ok", caller, principal) with
                {
                    AfterSha256 = snapshot.Sha256,
                    GrantId = issued.DownloadId,
                    ByteLength = snapshot.Bytes.LongLength,
                });
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Prepare MUST audit before a grant leaves the server; a grant
                // whose record could not be written is withdrawn.
                _store.Revoke(issued.DownloadId);
                auditFailed = true;
                throw new KnapperException(VaultErrorCode.IoError,
                    "the audit record for this grant could not be written, so no grant was issued", e);
            }
            return new DownloadPrepareItem(snapshot.Path,
                new DownloadGrant(issued.DownloadId, _urlBase + issued.DownloadId, issued.Token, issued.ExpiresAt,
                    snapshot.Path, snapshot.Sha256, snapshot.Bytes.LongLength, _fetch),
                null, null);
        }
        catch (KnapperException e)
        {
            // Refusals for a resolved path are audited like mutation
            // rejections; one that never resolved has no vault object to be
            // about. The CODE is the audit signal — never the message.
            if (relative is not null && !auditFailed)
                TryAudit(Entry(relative, e.Code.ToString(), caller, principal));
            return new DownloadPrepareItem(relative ?? item.Path, null, e.Code.ToString(), e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (relative is not null)
                TryAudit(Entry(relative, VaultErrorCode.IoError.ToString(), caller, principal));
            return new DownloadPrepareItem(relative ?? item.Path, null, VaultErrorCode.IoError.ToString(),
                "filesystem failure — transient or environmental; details in the server log");
        }
    }

    private static AuditLog.Entry Entry(string path, string outcome, AuditContext caller, TransferPrincipal principal) =>
        new(DateTimeOffset.UtcNow, Op, path, outcome, caller.Client, caller.RequestId,
            Principal: principal.ForAudit());

    /// <summary>
    /// A refusal's record is best-effort: the item already failed and nothing
    /// left the server. Exhaustive BY CONSTRUCTION, not by enumeration — a
    /// permission failure (<see cref="UnauthorizedAccessException"/>, which is
    /// not an IOException) once escaped a handler listing only the types seen
    /// so far and discarded a whole batch's per-item results (review
    /// 2026-11-08). AuditLog reports its own failures.
    /// </summary>
    private void TryAudit(AuditLog.Entry entry)
    {
        try
        {
            _audit.Append(entry);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
        }
    }
}
