using System.Security.Cryptography;
using Knapper.Core.Mutation;
using Knapper.Core.Options;

namespace Knapper.Core.Transfer;

/// <summary>The seven upload states (docs/proposals/transfer-grants.md §7).</summary>
public enum UploadState { Issued, Receiving, Ready, Committing, Succeeded, Failed, Expired }

/// <summary>What one upload grant is bound to: destination, mode and the exact source bytes.</summary>
public sealed record UploadBinding(string Path, string Mode, string? ExpectSha256, string SourceSha256, long ByteLength)
{
    public const string Create = "create";
    public const string Replace = "replace";
}

/// <summary>
/// In-memory upload grants (§4, §7, §10). A grant moves
/// issued → receiving → ready → committing → succeeded | failed, or to
/// expired; every transition happens under one lock, so two receivers can
/// never fill one buffer and a grant is committed at most once. Staged bytes
/// are immutable once accepted, and are the bytes the commit writes.
///
/// Single process, memory only: a restart invalidates every grant and replays
/// none, and an unknown ID is never reconstructed from a token. The byte
/// budget and record cap are the process-wide <see cref="TransferBudget"/>
/// shared with downloads.
/// </summary>
public sealed class UploadGrantStore : IDisposable
{
    private readonly UploadOptions _upload;
    private readonly TimeProvider _time;
    private readonly TransferBudget _budget;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Grant> _grants = new(StringComparer.Ordinal);
    private readonly ITimer _sweeper;
    private int _receiving;

    public UploadGrantStore(TransferOptions transfer, UploadOptions upload, TimeProvider? time = null, TransferBudget? budget = null)
    {
        _upload = upload;
        _time = time ?? TimeProvider.System;
        _budget = budget ?? new TransferBudget(transfer);
        _sweeper = _time.CreateTimer(_ => Sweep(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public sealed record Issued(string UploadId, string Token, DateTimeOffset ExpiresAt);

    internal sealed class Grant
    {
        public required string Id { get; init; }
        public required byte[] TokenDigest { get; init; }
        public required TransferPrincipal Principal { get; init; }
        public required UploadBinding Binding { get; init; }
        public required long ExpiresAtTimestamp { get; init; }
        public required DateTimeOffset ExpiresAtWall { get; init; }
        public UploadState State { get; set; } = UploadState.Issued;
        public long ReceiveDeadlineTimestamp { get; set; }
        public byte[]? Buffer { get; set; }
        public bool BytesHeld { get; set; } = true;

        /// <summary>
        /// A PUT handler holds this grant's receiver slot and is filling its
        /// buffer. While set, neither the slot nor the bytes go back to the
        /// budget — not even when the receive deadline fails the grant —
        /// because the handler's buffer is still live. Cleared exactly once,
        /// by <see cref="CompleteReceive"/> or <see cref="FailReceive"/>.
        /// </summary>
        public bool ReceiverHeld { get; set; }
        public long ForgetAtTimestamp { get; set; } = long.MaxValue;
        public MutationResult? Result { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
    }

    /// <summary>A read-only view of a grant, for status and for the outcome of a commit claim.</summary>
    public sealed record View(
        string UploadId,
        UploadState State,
        DateTimeOffset ExpiresAt,
        UploadBinding Binding,
        TransferPrincipal Principal,
        MutationResult? Result,
        string? ErrorCode,
        string? ErrorMessage);

    private View ViewOf(Grant g) =>
        new(g.Id, g.State, g.ExpiresAtWall, g.Binding, g.Principal, g.Result, g.ErrorCode, g.ErrorMessage);

    // ---- prepare -----------------------------------------------------------

    /// <summary>
    /// Issue a grant, reserving its record slot and declared bytes against the
    /// shared budget. Throws <see cref="VaultErrorCode.UploadCapacityExceeded"/>
    /// without holding anything when a limit is full; terminal records are
    /// never evicted to make room.
    /// </summary>
    public Issued Issue(TransferPrincipal principal, UploadBinding binding)
    {
        var lifetime = TimeSpan.FromSeconds(_upload.GrantLifetimeSeconds);
        lock (_lock)
        {
            SweepLocked();
            var active = _grants.Values.Count(IsActive);
            if (active >= _upload.MaxActiveGrants)
                throw Capacity($"{active} upload grants are active (Upload:MaxActiveGrants {_upload.MaxActiveGrants})");
            var mine = _grants.Values.Count(g => IsActive(g) && g.Principal == principal);
            if (mine >= _upload.MaxActiveGrantsPerPrincipal)
                throw Capacity($"this principal holds {mine} active upload grants (Upload:MaxActiveGrantsPerPrincipal)");
            if (_budget.TryTake(binding.ByteLength, 1) is { } why)
                throw Capacity(why);

            var id = DownloadGrantStore.NewSecret();
            var token = DownloadGrantStore.NewSecret();
            _grants[id] = new Grant
            {
                Id = id,
                TokenDigest = DownloadGrantStore.Digest(token)!,
                Principal = principal,
                Binding = binding,
                ExpiresAtTimestamp = _time.GetTimestamp() + Ticks(lifetime),
                // Monotonic time decides expiry; the wall clock is guidance.
                ExpiresAtWall = _time.GetUtcNow() + lifetime,
            };
            return new Issued(id, token, _time.GetUtcNow() + lifetime);
        }
    }

    /// <summary>Withdraw a grant that was never handed out (its audit append failed).</summary>
    public void Revoke(string uploadId)
    {
        lock (_lock)
        {
            if (_grants.Remove(uploadId, out var grant))
                _budget.Return(grant.BytesHeld ? grant.Binding.ByteLength : 0, 1);
        }
    }

    // ---- transfer ------------------------------------------------------------

    public enum ReceiveStatus { Ok, InvalidToken, Expired, AlreadyClaimed, Busy }

    /// <summary>
    /// The PUT's admission. Unknown IDs and wrong tokens answer
    /// <see cref="ReceiveStatus.InvalidToken"/> and never burn a valid grant;
    /// a full receiver pool answers <see cref="ReceiveStatus.Busy"/> and claims
    /// nothing. Otherwise the FIRST admitted PUT moves issued → receiving and
    /// every later one is <see cref="ReceiveStatus.AlreadyClaimed"/>.
    /// </summary>
    public (ReceiveStatus Status, View? Grant, TimeSpan Remaining) BeginReceive(string? uploadId, string? token)
    {
        var digest = DownloadGrantStore.Digest(token);
        lock (_lock)
        {
            SweepLocked();
            if (digest is null || uploadId is null || !_grants.TryGetValue(uploadId, out var grant)
                || !CryptographicOperations.FixedTimeEquals(digest, grant.TokenDigest))
            {
                return (ReceiveStatus.InvalidToken, null, TimeSpan.Zero);
            }
            if (grant.State == UploadState.Expired)
                return (ReceiveStatus.Expired, ViewOf(grant), TimeSpan.Zero);
            if (grant.State != UploadState.Issued)
                return (ReceiveStatus.AlreadyClaimed, ViewOf(grant), TimeSpan.Zero);
            if (_receiving >= _upload.MaxConcurrentReceives)
                return (ReceiveStatus.Busy, ViewOf(grant), TimeSpan.Zero);

            grant.State = UploadState.Receiving;
            var window = TimeSpan.FromSeconds(_upload.ReceiveTimeoutSeconds);
            grant.ReceiveDeadlineTimestamp = _time.GetTimestamp() + Ticks(window);
            grant.ReceiverHeld = true;
            _receiving++;
            return (ReceiveStatus.Ok, ViewOf(grant), window);
        }
    }

    /// <summary>
    /// Stage the received bytes: receiving → ready, and the handler's receiver
    /// lease ends. False when the receive deadline already failed the grant;
    /// the lease still ends here — slot and bytes go back to the budget now
    /// that the handler has let go of its buffer — and the bytes are discarded.
    /// </summary>
    public bool CompleteReceive(string uploadId, byte[] bytes)
    {
        lock (_lock)
        {
            if (!_grants.TryGetValue(uploadId, out var grant))
                return false;
            var staged = grant.State == UploadState.Receiving;
            if (staged)
            {
                grant.Buffer = bytes;
                grant.State = UploadState.Ready;
            }
            ReleaseReceiverLocked(grant);
            return staged;
        }
    }

    /// <summary>
    /// receiving → failed with the transfer's typed error, and the handler's
    /// receiver lease ends. Safe to call on every exit path, including after
    /// the deadline already failed the grant: the lease is released exactly
    /// once and a terminal grant is never re-labelled.
    /// </summary>
    public void FailReceive(string uploadId, VaultErrorCode code, string message)
    {
        lock (_lock)
        {
            if (!_grants.TryGetValue(uploadId, out var grant))
                return;
            if (grant.State == UploadState.Receiving)
                TerminateLocked(grant, UploadState.Failed, null, code.ToString(), message);
            ReleaseReceiverLocked(grant);
        }
    }

    private void ReleaseReceiverLocked(Grant grant)
    {
        if (!grant.ReceiverHeld)
            return;
        grant.ReceiverHeld = false;
        _receiving--;
        // Bytes a terminal grant still holds were kept only for this handler.
        if (grant.State is UploadState.Failed or UploadState.Expired && grant.BytesHeld)
        {
            _budget.Return(grant.Binding.ByteLength, 0);
            grant.BytesHeld = false;
        }
    }

    // ---- commit ----------------------------------------------------------------

    public enum CommitStatus { Ok, NotFound, NotReady, Expired, Committing, Terminal }

    /// <summary>
    /// The commit's claim: ready → committing, handing back the staged bytes,
    /// for the principal that prepared the grant and nobody else. A grant
    /// still issued or receiving is <see cref="CommitStatus.NotReady"/> and is
    /// NOT consumed; a duplicate during a commit is
    /// <see cref="CommitStatus.Committing"/>; after one,
    /// <see cref="CommitStatus.Terminal"/> with the cached outcome and no write.
    /// </summary>
    public (CommitStatus Status, View? Grant, byte[]? Bytes) BeginCommit(TransferPrincipal principal, string uploadId)
    {
        lock (_lock)
        {
            SweepLocked();
            if (!_grants.TryGetValue(uploadId, out var grant) || grant.Principal != principal)
                return (CommitStatus.NotFound, null, null);
            switch (grant.State)
            {
                case UploadState.Issued:
                case UploadState.Receiving:
                    return (CommitStatus.NotReady, ViewOf(grant), null);
                case UploadState.Committing:
                    return (CommitStatus.Committing, ViewOf(grant), null);
                case UploadState.Expired:
                    return (CommitStatus.Expired, ViewOf(grant), null);
                case UploadState.Succeeded:
                case UploadState.Failed:
                    return (CommitStatus.Terminal, ViewOf(grant), null);
            }
            grant.State = UploadState.Committing;
            return (CommitStatus.Ok, ViewOf(grant), grant.Buffer);
        }
    }

    /// <summary>
    /// committing → succeeded | failed, freeing the staged bytes. A failed
    /// mutation is terminal — never retried automatically: some failure paths
    /// leave visible side effects, and a stale base needs a fresh read.
    /// </summary>
    public void FinishCommit(string uploadId, MutationResult? result, string? errorCode, string? errorMessage)
    {
        lock (_lock)
        {
            if (!_grants.TryGetValue(uploadId, out var grant) || grant.State != UploadState.Committing)
                return;
            TerminateLocked(grant, result is not null ? UploadState.Succeeded : UploadState.Failed,
                result, errorCode, errorMessage);
        }
    }

    /// <summary>The principal's own view of a grant, or null — unknown and other-principal IDs look alike.</summary>
    public View? Status(TransferPrincipal principal, string uploadId)
    {
        lock (_lock)
        {
            SweepLocked();
            return _grants.TryGetValue(uploadId, out var grant) && grant.Principal == principal ? ViewOf(grant) : null;
        }
    }

    // ---- housekeeping -----------------------------------------------------------

    public long BufferedBytes => _budget.Bytes;

    public int TrackedCount
    {
        get
        {
            lock (_lock)
                return _grants.Count;
        }
    }

    public void Sweep()
    {
        lock (_lock)
            SweepLocked();
    }

    private void SweepLocked()
    {
        var now = _time.GetTimestamp();
        foreach (var (id, grant) in _grants.ToList())
        {
            switch (grant.State)
            {
                // Expiry stops a transfer or a commit from STARTING; it never
                // cancels one that has (committing is left alone).
                case UploadState.Issued or UploadState.Ready when now >= grant.ExpiresAtTimestamp:
                    TerminateLocked(grant, UploadState.Expired, null, nameof(VaultErrorCode.UploadExpired),
                        "the grant expired before it was used — prepare again");
                    break;
                case UploadState.Receiving when now >= grant.ReceiveDeadlineTimestamp:
                    // Failed now, so status and commit see the timeout — but
                    // the receiver slot and bytes stay counted until the
                    // handler, which may still be inside its read, lets go.
                    TerminateLocked(grant, UploadState.Failed, null, nameof(VaultErrorCode.UploadTransferTimeout),
                        "the body did not arrive within the receive deadline — prepare again");
                    break;
            }
            // Never forget a record whose receiver lease is still held: the
            // handler's release must find it, or the slot would leak.
            if (now >= grant.ForgetAtTimestamp && !grant.ReceiverHeld && _grants.Remove(id))
                _budget.Return(0, 1);
        }
    }

    private void TerminateLocked(Grant grant, UploadState state, MutationResult? result, string? code, string? message)
    {
        grant.State = state;
        grant.Result = result;
        grant.ErrorCode = code;
        grant.ErrorMessage = message;
        grant.Buffer = null;
        if (grant.BytesHeld && !grant.ReceiverHeld)
        {
            _budget.Return(grant.Binding.ByteLength, 0);
            grant.BytesHeld = false;
        }
        // Retention starts when the state becomes terminal, not on each poll.
        grant.ForgetAtTimestamp = _time.GetTimestamp() + Ticks(TimeSpan.FromSeconds(_upload.ReceiptRetentionSeconds));
    }

    private static bool IsActive(Grant g) =>
        g.State is UploadState.Issued or UploadState.Receiving or UploadState.Ready or UploadState.Committing;

    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * _time.TimestampFrequency);

    private static KnapperException Capacity(string why) =>
        new(VaultErrorCode.UploadCapacityExceeded, $"{why} — nothing was issued; send this item in a later round");

    public void Dispose() => _sweeper.Dispose();
}
