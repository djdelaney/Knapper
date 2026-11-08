using System.Buffers.Text;
using System.Security.Cryptography;
using Knapper.Core.Options;

namespace Knapper.Core.Transfer;

/// <summary>
/// In-memory download grants (docs/proposals/transfer-grants.md §5, §7, §10,
/// §11). Each grant holds an immutable snapshot taken at prepare; the GET
/// serves ONLY that snapshot, so this store — and the route in front of it —
/// opens no file, resolves no path, and calls no Core read. A defect in
/// download authentication can leak at most the snapshots of live grants.
///
/// Single process, memory only: a restart invalidates every grant, and an
/// unknown ID is never reconstructed from a token. Tokens are stored as
/// SHA-256 digests and compared in fixed time; the store never sees a token
/// it did not just mint except to hash it.
/// </summary>
public sealed class DownloadGrantStore : IDisposable
{
    public const int SecretBytes = 32;

    /// <summary>The base64url length of a 32-byte value, unpadded.</summary>
    public const int EncodedLength = 43;

    private readonly TransferOptions _transfer;
    private readonly DownloadOptions _download;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Grant> _grants = new(StringComparer.Ordinal);
    private readonly HashSet<Reservation> _pending = [];
    private readonly ITimer _sweeper;
    private readonly TransferBudget _budget;

    /// <param name="budget">
    /// The budget shared with the upload direction (one per process). Null
    /// gives this store a private one — tests only.
    /// </param>
    public DownloadGrantStore(
        TransferOptions transfer, DownloadOptions download, TimeProvider? time = null, TransferBudget? budget = null)
    {
        _transfer = transfer;
        _download = download;
        _time = time ?? TimeProvider.System;
        _budget = budget ?? new TransferBudget(transfer);
        // Expiry is swept on store access AND on a bounded timer, so an idle
        // server does not hold an abandoned snapshot past its lifetime.
        _sweeper = _time.CreateTimer(_ => Sweep(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public sealed record Issued(string DownloadId, string Token, DateTimeOffset ExpiresAt);

    public enum ClaimStatus { Ok, InvalidToken, Expired, AlreadyClaimed }

    /// <summary>
    /// Capacity held BEFORE a snapshot is read: the declared bytes count
    /// against the shared budget and the reservation takes an active slot from
    /// the moment it exists, so concurrent prepares can never allocate past
    /// <c>Transfer:MaxBufferedBytes</c> (review 2026-11-08: reading first and
    /// checking at issue let eight concurrent 5 MiB reads sit in memory over a
    /// full budget). <see cref="Issue(Reservation, byte[], string, string)"/>
    /// consumes it; disposing an unconsumed reservation releases it.
    /// </summary>
    public sealed class Reservation : IDisposable
    {
        private readonly DownloadGrantStore _owner;

        internal Reservation(DownloadGrantStore owner, TransferPrincipal principal, long bytes)
        {
            _owner = owner;
            Principal = principal;
            Bytes = bytes;
        }

        public TransferPrincipal Principal { get; }
        public long Bytes { get; }
        internal bool Done { get; set; }

        public void Dispose() => _owner.ReleaseReservation(this);
    }

    /// <summary>
    /// The result of a GET's claim. On <see cref="ClaimStatus.Ok"/> the caller
    /// owns <see cref="Bytes"/> and MUST call <see cref="Release"/> once the
    /// response is written (or abandoned): that is when the budget is freed.
    /// </summary>
    public sealed class Claim
    {
        private readonly DownloadGrantStore? _owner;
        private int _released;

        internal Claim(ClaimStatus status, byte[]? bytes, DownloadGrantStore? owner, Grant? grant = null)
        {
            Status = status;
            Bytes = bytes;
            _owner = owner;
            Principal = grant?.Principal;
            Path = grant?.Path;
            Sha256 = grant?.Sha256;
            DownloadId = grant?.Id;
        }

        public ClaimStatus Status { get; }
        public byte[]? Bytes { get; }

        /// <summary>
        /// The grant a VALID token named — set for Ok, Expired and
        /// AlreadyClaimed, so the outcome can be audited against it; null for
        /// InvalidToken, which proves nothing about any grant.
        /// </summary>
        public TransferPrincipal? Principal { get; }
        public string? Path { get; }
        public string? Sha256 { get; }
        public string? DownloadId { get; }

        public void Release()
        {
            if (Bytes is not null && _owner is not null && Interlocked.Exchange(ref _released, 1) == 0)
                _owner.FreeBytes(Bytes.LongLength);
        }
    }

    internal sealed class Grant
    {
        public required string Id { get; init; }
        public required string Path { get; init; }
        public required string Sha256 { get; init; }
        public required TransferPrincipal Principal { get; init; }
        public required byte[] TokenDigest { get; init; }
        public required long ExpiresAtTimestamp { get; init; }
        public required long ForgetAtTimestamp { get; init; }
        public byte[]? Snapshot { get; set; }   // null once served or expired
        public long Length { get; init; }
        public bool Served { get; set; }
    }

    /// <summary>
    /// Hold capacity for a snapshot of <paramref name="bytes"/> bytes before
    /// reading it. Throws <see cref="VaultErrorCode.DownloadCapacityExceeded"/>
    /// without holding anything when the byte budget or a slot limit is full
    /// — terminal records are never evicted to make room.
    /// </summary>
    public Reservation Reserve(TransferPrincipal principal, long bytes)
    {
        if (bytes < 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        lock (_lock)
        {
            SweepLocked();
            var active = ActiveLocked(null);
            if (active >= _download.MaxActiveGrants)
                throw Capacity($"{active} download grants are active or being prepared (Download:MaxActiveGrants {_download.MaxActiveGrants})");
            var mine = ActiveLocked(principal);
            if (mine >= _download.MaxActiveGrantsPerPrincipal)
                throw Capacity($"this principal holds {mine} active download grants (Download:MaxActiveGrantsPerPrincipal)");
            // One record slot (held for the grant's whole retained life) and
            // the declared bytes (held until served, expired or revoked).
            if (_budget.TryTake(bytes, 1) is { } why)
                throw Capacity(why);
            var reservation = new Reservation(this, principal, bytes);
            _pending.Add(reservation);
            return reservation;
        }
    }

    /// <summary>
    /// Turn a reservation into a grant over an immutable snapshot of exactly
    /// the reserved size. The array is owned by the store from here on.
    /// </summary>
    public Issued Issue(Reservation reservation, byte[] snapshot, string path, string sha256)
    {
        var lifetime = TimeSpan.FromSeconds(_download.GrantLifetimeSeconds);
        lock (_lock)
        {
            if (reservation.Done || !_pending.Contains(reservation))
                throw new InvalidOperationException("the reservation was already consumed or released");
            if (snapshot.LongLength != reservation.Bytes)
                throw new InvalidOperationException($"snapshot is {snapshot.LongLength} bytes; {reservation.Bytes} were reserved");
            _pending.Remove(reservation);
            reservation.Done = true; // its bytes now belong to the grant

            var id = NewSecret();
            var token = NewSecret();
            var lifetimeTicks = (long)(lifetime.TotalSeconds * _time.TimestampFrequency);
            var expires = _time.GetTimestamp() + lifetimeTicks;
            _grants[id] = new Grant
            {
                Id = id,
                Path = path,
                Sha256 = sha256,
                Principal = reservation.Principal,
                TokenDigest = Digest(token)!,
                ExpiresAtTimestamp = expires,
                // Terminal records are retained one more lifetime, so a late
                // GET is told 410/409 rather than an indistinct 401.
                ForgetAtTimestamp = expires + lifetimeTicks,
                Snapshot = snapshot,
                Length = snapshot.LongLength,
            };
            // Monotonic time decides expiry; this wall-clock value is guidance.
            return new Issued(id, token, _time.GetUtcNow() + lifetime);
        }
    }

    /// <summary>Reserve and issue in one step, for a snapshot already in hand (tests).</summary>
    internal Issued Issue(TransferPrincipal principal, byte[] snapshot, string path = "test", string sha256 = "")
    {
        var reservation = Reserve(principal, snapshot.LongLength);
        try
        {
            return Issue(reservation, snapshot, path, sha256);
        }
        finally
        {
            reservation.Dispose();
        }
    }

    private int ActiveLocked(TransferPrincipal? principal) =>
        _grants.Values.Count(g => !g.Served && g.Snapshot is not null && (principal is null || g.Principal == principal))
        + _pending.Count(r => principal is null || r.Principal == principal);

    private void ReleaseReservation(Reservation reservation)
    {
        lock (_lock)
        {
            if (reservation.Done)
                return;
            reservation.Done = true;
            if (_pending.Remove(reservation))
                _budget.Return(reservation.Bytes, 1);
        }
    }

    /// <summary>
    /// Withdraw a grant that was never handed out (its audit append failed).
    /// Frees its bytes; a no-op for an unknown ID.
    /// </summary>
    public void Revoke(string downloadId)
    {
        lock (_lock)
        {
            if (_grants.Remove(downloadId, out var grant))
                _budget.Return(grant.Snapshot is not null ? grant.Length : 0, 1);
        }
    }

    /// <summary>
    /// The GET's single claim. Unknown IDs and wrong tokens answer the same
    /// <see cref="ClaimStatus.InvalidToken"/> and never burn a valid grant.
    /// The FIRST admitted claim takes the snapshot; every later one is
    /// <see cref="ClaimStatus.AlreadyClaimed"/>.
    /// </summary>
    public Claim TryClaim(string? downloadId, string? token)
    {
        var digest = Digest(token);
        lock (_lock)
        {
            SweepLocked();
            if (digest is null || downloadId is null || !_grants.TryGetValue(downloadId, out var grant)
                || !CryptographicOperations.FixedTimeEquals(digest, grant.TokenDigest))
            {
                return new Claim(ClaimStatus.InvalidToken, null, null);
            }
            if (grant.Served)
                return new Claim(ClaimStatus.AlreadyClaimed, null, null, grant);
            if (grant.Snapshot is null || _time.GetTimestamp() >= grant.ExpiresAtTimestamp)
            {
                ExpireLocked(grant);
                return new Claim(ClaimStatus.Expired, null, null, grant);
            }
            var bytes = grant.Snapshot;
            grant.Snapshot = null;
            grant.Served = true;
            // The bytes stay counted against the budget until Release.
            return new Claim(ClaimStatus.Ok, bytes, this, grant);
        }
    }

    /// <summary>Bytes currently counted against the budget (tests, metrics; the shared total when shared).</summary>
    public long BufferedBytes => _budget.Bytes;

    /// <summary>Tracked records, active and terminal (tests, metrics).</summary>
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
        // At expiry the snapshot is dropped and its bytes freed; the metadata
        // record stays one more lifetime so a late GET hears 410 (expired) or
        // 409 (already served) instead of an indistinct 401.
        foreach (var (id, grant) in _grants.ToList())
        {
            if (now >= grant.ExpiresAtTimestamp)
                ExpireLocked(grant);
            if (now >= grant.ForgetAtTimestamp && _grants.Remove(id))
                _budget.Return(0, 1);
        }
    }

    private void ExpireLocked(Grant grant)
    {
        if (grant.Snapshot is not null)
        {
            _budget.Return(grant.Length, 0);
            grant.Snapshot = null;
        }
    }

    private void FreeBytes(long length) => _budget.Return(length, 0);

    private static KnapperException Capacity(string why) =>
        new(VaultErrorCode.DownloadCapacityExceeded, $"{why} — nothing was issued; try again shortly or in a later round");

    internal static string NewSecret() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>Digest of a well-formed encoded secret, or null for anything malformed.</summary>
    internal static byte[]? Digest(string? encoded)
    {
        if (encoded is null || encoded.Length != EncodedLength)
            return null;
        Span<byte> raw = stackalloc byte[SecretBytes + 2];
        if (Base64Url.DecodeFromChars(encoded, raw, out _, out var written) != System.Buffers.OperationStatus.Done
            || written != SecretBytes)
        {
            return null;
        }
        return SHA256.HashData(raw[..SecretBytes]);
    }

    /// <summary>True when a string has the shape of a grant ID or token (43 base64url characters).</summary>
    public static bool IsWellFormed(string? encoded) => Digest(encoded) is not null;

    public void Dispose() => _sweeper.Dispose();
}
