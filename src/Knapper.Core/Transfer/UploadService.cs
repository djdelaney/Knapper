using Knapper.Core.Git;
using Knapper.Core.Mutation;
using Knapper.Core.Options;
using Knapper.Core.Vault;

namespace Knapper.Core.Transfer;

public sealed record UploadPrepareRequest(string Path, string SourceSha256, long ByteLength, string Mode, string? ExpectSha256);

/// <summary>
/// The upload tools' bodies (docs/proposals/transfer-grants.md §4). Prepare
/// binds a destination and exact source bytes into a grant; the public PUT
/// only stages bytes; commit lands them through the ordinary mutation path —
/// <see cref="VaultMutationService.CreateBytes"/> or the conditional
/// <see cref="VaultMutationService.ReplaceBytes"/> — after the same secret
/// scan the git committer runs. A grant is bound to the principal that
/// prepared it; nothing here takes an alternative path, bytes or hash at
/// commit.
/// </summary>
public sealed class UploadService
{
    public const int MaxItems = 16;
    private const string PrepareOp = "upload_prepare";
    private const string CommitOp = "upload_commit";

    private readonly VaultPathResolver _resolver;
    private readonly VaultMutationService _mutations;
    private readonly UploadGrantStore _store;
    private readonly AuditLog _audit;
    private readonly ArchivedPrefixes _archived;
    private readonly UploadOptions _upload;
    private readonly SyncOptions _sync;
    private readonly string _urlBase;
    private readonly string _send;

    public UploadService(
        VaultPathResolver resolver, VaultMutationService mutations, UploadGrantStore store, AuditLog audit,
        ArchivedPrefixes archived, TransferOptions transfer, UploadOptions upload, SyncOptions sync)
    {
        _resolver = resolver;
        _mutations = mutations;
        _store = store;
        _audit = audit;
        _archived = archived;
        _upload = upload;
        _sync = sync;
        _urlBase = transfer.PublicOrigin.TrimEnd('/') + TransferOptions.PathPrefix + "/uploads/";
        _send = SendHint(
            transfer.PublicOrigin.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "http" : "https",
            upload.ReceiveTimeoutSeconds);
    }

    /// <summary>
    /// The recipe, as fixed text with its prose as shell comments, so the
    /// whole string runs as written (§6). `--max-time` matches the server's
    /// receive deadline. The URL ends in the upload ID, never a slash: with
    /// <c>-T</c>, curl appends the local file's name to a URL ending in `/`.
    /// </summary>
    internal static string SendHint(string proto, int receiveTimeoutSeconds) =>
        "# Set TOKEN=<uploadToken> URL=<uploadUrl> SRC=<the local file you hashed>, then run this whole text\n" +
        "# in bash or zsh. The token goes to curl on stdin, never on the command line. The grant works ONCE.\n" +
        "# Nothing lands until vault_upload_commit. On curl exit 28/52/55/56 call vault_upload_status first.\n" +
        "printf 'Authorization: KnapperUpload %s\\n' \"$TOKEN\" |\n" +
        $"  curl --fail-with-body -sS --proto '={proto}' --max-redirs 0 --max-time {receiveTimeoutSeconds} \\\n" +
        "       -T \"$SRC\" -H 'Content-Type: application/octet-stream' -H @- \"$URL\"\n";

    // ---- prepare ------------------------------------------------------------

    public UploadPrepareResult Prepare(
        TransferPrincipal? principal, IReadOnlyList<UploadPrepareRequest>? items, AuditContext caller)
    {
        RequireEnabled();
        var who = RequirePrincipal(principal);
        if (items is null || items.Count is < 1 or > MaxItems)
            throw new KnapperException(VaultErrorCode.InvalidArgument, $"items must hold 1–{MaxItems} entries, got {items?.Count ?? 0}");

        // Duplicate destinations are refused for the whole call, as in
        // vault_batch — judged on RESOLVED paths, so two spellings of one
        // file cannot slip past.
        var resolved = items.Select(i => TryResolve(i?.Path)).ToList();
        var duplicate = resolved.Where(r => r is not null).GroupBy(r => r!, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new KnapperException(VaultErrorCode.InvalidArgument, $"duplicate destination path in one call: {duplicate.Key}");

        var issued = new List<string>();
        try
        {
            var results = new List<UploadPrepareItem>(items.Count);
            foreach (var item in items)
            {
                var result = PrepareOne(who, item, caller);
                if (result.Grant is { } grant)
                    issued.Add(grant.UploadId);
                results.Add(result);
            }
            return new UploadPrepareResult(results);
        }
        catch
        {
            foreach (var id in issued)
                _store.Revoke(id);
            throw;
        }
    }

    private UploadPrepareItem PrepareOne(TransferPrincipal principal, UploadPrepareRequest? item, AuditContext caller)
    {
        string? relative = null;
        var auditFailed = false;
        try
        {
            if (item is null)
                throw new KnapperException(VaultErrorCode.InvalidArgument, "item is null");
            var vp = _resolver.Resolve(item.Path);
            relative = vp.Relative;
            var binding = Bind(item, relative);
            CheckDestination(vp, binding);

            var issued = _store.Issue(principal, binding);
            try
            {
                _audit.Append(Entry(PrepareOp, relative, "ok", caller, principal) with
                {
                    BeforeSha256 = binding.ExpectSha256,
                    AfterSha256 = binding.SourceSha256,
                    GrantId = issued.UploadId,
                    ByteLength = binding.ByteLength,
                    Detail = binding.Mode,
                });
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Prepare MUST audit before a grant leaves the server.
                _store.Revoke(issued.UploadId);
                auditFailed = true;
                throw new KnapperException(VaultErrorCode.IoError,
                    "the audit record for this grant could not be written, so no grant was issued", e);
            }
            return new UploadPrepareItem(relative,
                new UploadGrant(issued.UploadId, _urlBase + issued.UploadId, issued.Token, issued.ExpiresAt,
                    relative, binding.SourceSha256, binding.ByteLength, binding.Mode, binding.ExpectSha256, _send),
                null, null);
        }
        catch (KnapperException e)
        {
            if (relative is not null && !auditFailed)
                TryAudit(Entry(PrepareOp, relative, e.Code.ToString(), caller, principal));
            return new UploadPrepareItem(relative ?? item?.Path ?? "", null, e.Code.ToString(), e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (relative is not null)
                TryAudit(Entry(PrepareOp, relative, nameof(VaultErrorCode.IoError), caller, principal));
            return new UploadPrepareItem(relative ?? item?.Path ?? "", null, nameof(VaultErrorCode.IoError),
                "filesystem failure — transient or environmental; details in the server log");
        }
    }

    private UploadBinding Bind(UploadPrepareRequest item, string relative)
    {
        var mode = item.Mode?.Trim().ToLowerInvariant();
        if (mode is not (UploadBinding.Create or UploadBinding.Replace))
            throw new KnapperException(VaultErrorCode.InvalidArgument, "mode must be \"create\" or \"replace\"");
        var source = NormalizeSha(item.SourceSha256, "sourceSha256")
            ?? throw new KnapperException(VaultErrorCode.InvalidArgument, "sourceSha256 is required");
        if (item.ByteLength < 0)
            throw new KnapperException(VaultErrorCode.InvalidArgument, "byteLength must be zero or more");
        if (item.ByteLength > _sync.MaxFileBytes)
        {
            throw new KnapperException(VaultErrorCode.TooLargeToSync,
                $"{relative} would be {item.ByteLength} bytes; Obsidian Sync carries at most {_sync.MaxFileBytes} " +
                "(Sync__MaxFileBytes), so it would never reach any device");
        }

        string? expect = null;
        if (mode == UploadBinding.Create)
        {
            if (!string.IsNullOrEmpty(item.ExpectSha256))
                throw new KnapperException(VaultErrorCode.InvalidArgument, "create mode takes no expectSha256 — the destination must be absent");
        }
        else
        {
            if (!_upload.AllowReplace)
            {
                throw new KnapperException(VaultErrorCode.UploadReplacementDisabled,
                    "whole-file replacement is off on this deployment (Upload:AllowReplace) — change an existing " +
                    "file with vault_edit instead");
            }
            expect = NormalizeSha(item.ExpectSha256, "expectSha256")
                ?? throw new KnapperException(VaultErrorCode.InvalidArgument,
                    "replace mode requires expectSha256 from a fresh read — never infer or fetch one");
        }
        return new UploadBinding(relative, mode, expect, source, item.ByteLength);
    }

    /// <summary>
    /// ADVISORY checks: a grant that is doomed is cheaper refused now than
    /// after a transfer. Commit checks all of it again under the lock —
    /// prepare neither reserves the path nor replaces those checks.
    /// </summary>
    private void CheckDestination(VaultPath vp, UploadBinding binding)
    {
        if (Directory.Exists(vp.Absolute))
            throw new KnapperException(VaultErrorCode.InvalidArgument, $"path is a directory: {vp.Relative}");
        if (!Directory.Exists(Path.GetDirectoryName(vp.Absolute)!))
            throw new KnapperException(VaultErrorCode.NotFound, $"parent directory does not exist for {vp.Relative} — create it first with vault_mkdir");
        var exists = File.Exists(vp.Absolute);
        if (binding.Mode == UploadBinding.Create && exists)
            throw new KnapperException(VaultErrorCode.AlreadyExists, $"already exists: {vp.Relative}");
        if (binding.Mode == UploadBinding.Replace)
        {
            if (!exists)
                throw new KnapperException(VaultErrorCode.NotFound, $"no such file to replace: {vp.Relative}");
            if (_archived.Covers(vp.Relative))
            {
                throw new KnapperException(VaultErrorCode.PathArchived,
                    $"'{vp.Relative}' is in an archived subtree: replacing it is refused (creating into one is allowed)");
            }
        }
    }

    // ---- commit -------------------------------------------------------------

    public UploadCommitResult Commit(TransferPrincipal? principal, IReadOnlyList<string>? uploadIds, AuditContext caller)
    {
        RequireEnabled();
        var who = RequirePrincipal(principal);
        var ids = RequireIds(uploadIds);

        // Claim every grant first, then commit the claimed ones one after
        // another in path order, each an independent mutation: not atomic
        // across files, and never all-or-nothing — one stale photo must not
        // fail fifteen good ones.
        //
        // The staged bytes live in a ClaimedUpload holder, never in a tuple
        // or a sort buffer: each item's buffer is dropped the moment its
        // commit ends and BEFORE its budget is returned, or a later item
        // waiting on a lock would keep a returned-but-still-live buffer in
        // memory while new transfers allocate against that capacity
        // (review 2026-11-08).
        var claims = ids.Select(id => new ClaimedUpload(id, _store.BeginCommit(who, id))).ToList();
        var outcomes = new Dictionary<string, UploadCommitItem>(StringComparer.Ordinal);
        foreach (var claim in claims
            .Where(c => c.Status == UploadGrantStore.CommitStatus.Ok)
            .OrderBy(c => c.Grant!.Binding.Path, StringComparer.Ordinal))
        {
            outcomes[claim.Id] = CommitOne(who, claim, caller);
        }

        return new UploadCommitResult([.. claims.Select(c => outcomes.TryGetValue(c.Id, out var done)
            ? done
            : NotCommitted(c.Id, c.Status, c.Grant))]);
    }

    /// <summary>One claimed grant, holding its staged bytes only until <see cref="TakeBytes"/>.</summary>
    private sealed class ClaimedUpload
    {
        private byte[]? _bytes;

        public ClaimedUpload(string id, (UploadGrantStore.CommitStatus Status, UploadGrantStore.View? Grant, byte[]? Bytes) claim)
        {
            Id = id;
            Status = claim.Status;
            Grant = claim.Grant;
            _bytes = claim.Bytes;
        }

        public string Id { get; }
        public UploadGrantStore.CommitStatus Status { get; }
        public UploadGrantStore.View? Grant { get; }

        /// <summary>Hand the bytes over exactly once; the holder keeps no reference.</summary>
        public byte[] TakeBytes() => Interlocked.Exchange(ref _bytes, null)
            ?? throw new InvalidOperationException("the staged bytes were already taken");
    }

    private UploadCommitItem CommitOne(TransferPrincipal principal, ClaimedUpload claim, AuditContext caller)
    {
        var grant = claim.Grant!;
        var bytes = claim.TakeBytes();
        var binding = grant.Binding;
        var ctx = caller with { GrantId = grant.UploadId, Principal = principal.ForAudit() };
        MutationResult? result = null;
        string? code = null;
        string? message = null;
        try
        {
            // The committer's own rule, before any temp file exists: one
            // credential-shaped string here would refuse EVERY vault commit
            // until a human edits it (§4.3).
            var findings = SecretScanPolicy.Scan(binding.Path, bytes);
            if (findings.Count > 0)
            {
                code = nameof(VaultErrorCode.SecretDetected);
                message = "credential-shaped content in the payload — it would block every vault commit, so it was " +
                          "not written: " + string.Join("; ", findings.Take(5).Select(f => $"line {f.Line} {f.Kind} ({f.Masked})"));
                TryAudit(Entry(CommitOp, binding.Path, code, ctx, principal) with { GrantId = grant.UploadId });
            }
            else
            {
                result = binding.Mode == UploadBinding.Create
                    ? _mutations.CreateBytes(binding.Path, bytes, ctx)
                    : _mutations.ReplaceBytes(binding.Path, binding.ExpectSha256!, bytes, ctx);
                if (!string.Equals(result.NewSha256, binding.SourceSha256, StringComparison.Ordinal))
                {
                    // Cannot happen: the bytes were hashed against the grant at
                    // the PUT and are immutable since. Said loudly if it ever does.
                    code = nameof(VaultErrorCode.VerifyFailed);
                    message = $"the committed file hashes to {result.NewSha256}, not the source hash {binding.SourceSha256}";
                    result = null;
                }
            }
        }
        catch (KnapperException e)
        {
            code = e.Code.ToString();
            message = e.Message;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Exhaustive: the grant must leave `committing` on every path, or
            // it would answer "committing" until retention ended.
            code = "Internal";
            message = "unexpected server error during commit — see server logs";
            _ = e;
        }
        finally
        {
            // Drop the last reference BEFORE the capacity goes back: the
            // budget must never count memory as free while it is still live.
            bytes = null;
            _store.FinishCommit(grant.UploadId, result, result is null ? code : null, result is null ? message : null);
        }
        return result is not null
            ? new UploadCommitItem(grant.UploadId, Wire(UploadState.Succeeded), result, null, null)
            : new UploadCommitItem(grant.UploadId, Wire(UploadState.Failed), null, code, message);
    }

    private static UploadCommitItem NotCommitted(string id, UploadGrantStore.CommitStatus status, UploadGrantStore.View? grant) =>
        status switch
        {
            UploadGrantStore.CommitStatus.NotFound => new(id, "unknown", null, nameof(VaultErrorCode.UploadNotFound),
                "no such upload for this principal"),
            UploadGrantStore.CommitStatus.NotReady => new(id, Wire(grant!.State), null, nameof(VaultErrorCode.UploadNotReady),
                "the bytes have not arrived yet (or are still arriving); nothing was consumed — send them, then commit"),
            UploadGrantStore.CommitStatus.Expired => new(id, Wire(UploadState.Expired), null, nameof(VaultErrorCode.UploadExpired),
                "the grant expired before it was committed — prepare again"),
            // A duplicate while the first commit runs: neither result nor error.
            UploadGrantStore.CommitStatus.Committing => new(id, Wire(UploadState.Committing), null, null, null),
            // Already terminal: the cached outcome, with no second write. It
            // describes that commit, not necessarily the vault now.
            _ => new(id, Wire(grant!.State), grant.Result, grant.ErrorCode, grant.ErrorMessage),
        };

    // ---- status -------------------------------------------------------------

    public UploadStatusResult Status(TransferPrincipal? principal, IReadOnlyList<string>? uploadIds)
    {
        RequireEnabled();
        var who = RequirePrincipal(principal);
        var ids = RequireIds(uploadIds);
        return new UploadStatusResult([.. ids.Select(id => _store.Status(who, id) is { } v
            ? new UploadStatusItem(id, Wire(v.State), v.ExpiresAt, v.Binding.Path, v.Binding.SourceSha256,
                v.Binding.ByteLength, v.Result, v.ErrorCode, v.ErrorMessage)
            : new UploadStatusItem(id, "unknown", null, null, null, null, null,
                nameof(VaultErrorCode.UploadNotFound), "no such upload for this principal"))]);
    }

    // ---- shared -------------------------------------------------------------

    public static string Wire(UploadState state) => state.ToString().ToLowerInvariant();

    private void RequireEnabled()
    {
        if (!_upload.Enabled)
            throw new KnapperException(VaultErrorCode.UploadDisabled, "uploads are turned off on this deployment (Upload:Enabled)");
    }

    private static TransferPrincipal RequirePrincipal(TransferPrincipal? principal) =>
        principal ?? throw new KnapperException(VaultErrorCode.UploadNotPermitted,
            "this request carries no verified principal to bind an upload to — uploads need an authenticated " +
            "Access identity (or Transfer:LocalOnly on a same-machine deployment)");

    private static IReadOnlyList<string> RequireIds(IReadOnlyList<string>? ids)
    {
        if (ids is null || ids.Count is < 1 or > MaxItems)
            throw new KnapperException(VaultErrorCode.InvalidArgument, $"uploadIds must hold 1–{MaxItems} entries, got {ids?.Count ?? 0}");
        if (ids.Any(string.IsNullOrWhiteSpace))
            throw new KnapperException(VaultErrorCode.InvalidArgument, "each upload ID must be non-empty");
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count)
            throw new KnapperException(VaultErrorCode.InvalidArgument, "duplicate upload ID in one call");
        return ids;
    }

    private string? TryResolve(string? path)
    {
        try
        {
            return path is null ? null : _resolver.Resolve(path).Relative;
        }
        catch (KnapperException)
        {
            return null; // the item reports its own InvalidPath
        }
    }

    private static string? NormalizeSha(string? sha, string name)
    {
        if (string.IsNullOrEmpty(sha))
            return null;
        if (sha.Length != 64 || !sha.All(Uri.IsHexDigit))
            throw new KnapperException(VaultErrorCode.InvalidArgument, $"{name} must be exactly 64 hex characters");
        return sha.ToLowerInvariant();
    }

    private static AuditLog.Entry Entry(string op, string path, string outcome, AuditContext caller, TransferPrincipal principal) =>
        new(DateTimeOffset.UtcNow, op, path, outcome, caller.Client, caller.RequestId, Principal: principal.ForAudit());

    private void TryAudit(AuditLog.Entry entry)
    {
        try
        {
            _audit.Append(entry);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Best-effort for refusals: exhaustive by construction.
        }
    }
}
