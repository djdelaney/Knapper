using Knapper.Core.Interop;
using Knapper.Core.Options;
using Knapper.Core.Vault;
using Microsoft.Win32.SafeHandles;

namespace Knapper.Core.Transfer;

/// <summary>
/// Takes the immutable snapshot a download grant serves
/// (docs/proposals/transfer-grants.md §5.1). The ONLY place the download
/// direction reads vault bytes, and it reads them once, at prepare: the GET
/// route never opens a file.
///
/// The torn-read guard is by CONTENT, the way this codebase proves
/// everything: the file is read twice through the same descriptor, and the
/// snapshot is served only when both reads agree byte for byte and the
/// descriptor's size and mtime did not move across them. A read that
/// straddles an in-place write by Sync or a shell returns bytes from two
/// versions, and its hash describes a file that never existed on disk; a
/// second full read of the same descriptor disagrees with it unless the
/// writer stalled in exactly the same way twice. The stat comparison only
/// ever drives a RETRY, never a decision to serve (CLAUDE.md's `Replace`
/// rule: a stat tuple is not proof of content).
/// </summary>
public sealed class DownloadSnapshotReader(VaultPathResolver resolver, SyncOptions sync, DownloadOptions download)
{
    /// <summary>Re-reads before giving up on a file that will not settle.</summary>
    internal const int MaxAttempts = 4;

    private readonly ArchivedPrefixes _allowed = new(download.AllowedPrefixes);

    /// <summary>
    /// The snapshot, and — when the caller supplied a reserve callback — the
    /// capacity reservation that was taken BEFORE its bytes were allocated.
    /// The caller owns the reservation: consume it or dispose it.
    /// </summary>
    public sealed record Snapshot(string Path, byte[] Bytes, string Sha256, DownloadGrantStore.Reservation? Reservation = null);

    private const int CompareChunk = 64 * 1024;

    /// <summary>Test seam: runs between the two reads of an attempt. Never set outside tests.</summary>
    internal Action<int>? BetweenReadsTestHook;

    /// <summary>The vault-relative form of a path, after the resolver gate (for audit lines on refusal).</summary>
    public string Relative(string path) => resolver.Resolve(path).Relative;

    /// <param name="reserve">
    /// Called with the file's size BEFORE any buffer is allocated, and again
    /// if the size changes between attempts; it throws when capacity is full,
    /// so a refused prepare never allocates. Null only in tests.
    /// </param>
    public Snapshot Read(string path, string? expectSha256, Func<long, DownloadGrantStore.Reservation>? reserve = null)
    {
        var expected = NormalizeSha(expectSha256);
        var vp = resolver.Resolve(path);
        if (!_allowed.Covers(vp.Relative))
        {
            throw new KnapperException(VaultErrorCode.DownloadNotPermitted,
                $"{vp.Relative} is outside Download:AllowedPrefixes — downloads serve only the prefixes the " +
                "operator configured, and an empty list serves nothing");
        }
        if (Directory.Exists(vp.Absolute))
            throw new KnapperException(VaultErrorCode.InvalidArgument, $"path is a directory: {vp.Relative}");

        // Non-blocking, no-follow, typed by fstat on the descriptor: a FIFO or
        // a symlink swapped in since Resolve can neither hang nor be followed.
        using var handle = Posix.OpenRegularForRead(vp.Absolute, vp.Relative);
        var (bytes, reservation) = ReadSettled(handle, vp.Relative, reserve);
        try
        {
            var sha = VaultHash.Sha256Hex(bytes);
            if (expected is not null && !string.Equals(expected, sha, StringComparison.Ordinal))
            {
                throw new KnapperException(VaultErrorCode.PreconditionFailed,
                    $"{vp.Relative} has sha256 {sha}, not the expected {expected} — it changed since your read; " +
                    "nothing was issued");
            }
            return new Snapshot(vp.Relative, bytes, sha, reservation);
        }
        catch
        {
            reservation?.Dispose();
            throw;
        }
    }

    private (byte[] Bytes, DownloadGrantStore.Reservation? Reservation) ReadSettled(
        SafeFileHandle handle, string relative, Func<long, DownloadGrantStore.Reservation>? reserve)
    {
        DownloadGrantStore.Reservation? held = null;
        try
        {
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var before = Posix.FStat(handle);
                if (before.Size > sync.MaxFileBytes)
                {
                    throw new KnapperException(VaultErrorCode.TooLarge,
                        $"{relative} is {before.Size} bytes; transfers are bounded by Sync:MaxFileBytes " +
                        $"({sync.MaxFileBytes}), the largest file Sync carries");
                }
                // Capacity first, allocation second: the budget is what bounds
                // memory, so nothing the size of the file exists until it is held.
                if (reserve is not null && (held is null || held.Bytes != before.Size))
                {
                    held?.Dispose();
                    held = null;
                    held = reserve(before.Size);
                }
                var first = ReadExact(handle, before.Size);
                BetweenReadsTestHook?.Invoke(attempt);
                var agrees = first is not null && StillOnDisk(handle, first);
                var after = Posix.FStat(handle);
                if (agrees
                    && before.Size == after.Size
                    && before.MtimeSec == after.MtimeSec && before.MtimeNsec == after.MtimeNsec)
                {
                    var settled = held;
                    held = null;
                    return (first!, settled);
                }
            }
            throw new KnapperException(VaultErrorCode.IoError,
                $"{relative} kept changing while it was being read ({MaxAttempts} attempts) — an external writer is " +
                "active; try again once it settles");
        }
        finally
        {
            held?.Dispose();
        }
    }

    /// <summary>
    /// The whole file from offset 0, or null when it is not exactly
    /// <paramref name="size"/> bytes (it shrank, or a probe byte past the end
    /// shows it grew — retry). Allocates exactly the reserved size.
    /// </summary>
    private static byte[]? ReadExact(SafeFileHandle handle, long size)
    {
        var buffer = new byte[size];
        long total = 0;
        while (total < size)
        {
            var read = RandomAccess.Read(handle, buffer.AsSpan((int)total), total);
            if (read == 0)
                return null;
            total += read;
        }
        return Grew(handle, size) ? null : buffer;
    }

    /// <summary>
    /// The second, verifying read: compares the descriptor's current bytes
    /// against <paramref name="expected"/> in bounded chunks, so verification
    /// never holds a second copy of the file.
    /// </summary>
    private static bool StillOnDisk(SafeFileHandle handle, byte[] expected)
    {
        var chunk = new byte[Math.Min(CompareChunk, Math.Max(expected.Length, 1))];
        long offset = 0;
        while (offset < expected.LongLength)
        {
            var want = (int)Math.Min(chunk.Length, expected.LongLength - offset);
            var read = RandomAccess.Read(handle, chunk.AsSpan(0, want), offset);
            if (read == 0 || !chunk.AsSpan(0, read).SequenceEqual(expected.AsSpan((int)offset, read)))
                return false;
            offset += read;
        }
        return !Grew(handle, expected.LongLength);
    }

    private static bool Grew(SafeFileHandle handle, long size)
    {
        Span<byte> probe = stackalloc byte[1];
        return RandomAccess.Read(handle, probe, size) > 0;
    }

    private static string? NormalizeSha(string? sha)
    {
        if (sha is null)
            return null;
        if (sha.Length != 64 || !sha.All(Uri.IsHexDigit))
        {
            throw new KnapperException(VaultErrorCode.InvalidArgument,
                "expectSha256 must be exactly 64 hex characters (or omitted)");
        }
        return sha.ToLowerInvariant();
    }
}
