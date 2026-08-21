using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Knapper.Core.Interop;

/// <summary>
/// The small POSIX surface the safety primitives need and the BCL doesn't
/// expose: flock(2) advisory locks, link(2) no-clobber commits, directory
/// fsync, and realpath(3). Linux (production LXC) and macOS (dev) only —
/// the lock and atomic-commit semantics here are what the whole mutation
/// contract stands on, and there is no Windows story by design.
/// </summary>
internal static partial class Posix
{
    // Identical values on Linux and macOS.
    internal const int LOCK_SH = 1;
    internal const int LOCK_EX = 2;
    internal const int LOCK_NB = 4;
    internal const int LOCK_UN = 8;

    private const int EEXIST = 17;
    private const int EINTR = 4;
    // EWOULDBLOCK: EAGAIN(11) on Linux, 35 on macOS.
    private static readonly int[] WouldBlock = [11, 35];

    [LibraryImport("libc", SetLastError = true)]
    private static partial int flock(SafeFileHandle fd, int operation);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int link(string oldPath, string newPath);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int rename(string oldPath, string newPath);

    // Atomic pathname swap. Linux spells it renameat2(2) with RENAME_EXCHANGE
    // (kernel ≥3.15, glibc ≥2.28 — both ancient next to .NET 10's floor);
    // macOS spells it renamex_np(2) with RENAME_SWAP. Same flag value on both,
    // but keep the constants separate: they belong to different syscalls.
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int renameat2(int oldDirFd, string oldPath, int newDirFd, string newPath, uint flags);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int renamex_np(string oldPath, string newPath, uint flags);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int linkat(int oldDirFd, string oldPath, int newDirFd, string newPath, int flags);

    // Non-following stat. Linux uses statx(2): its struct layout is defined
    // by the UAPI to be IDENTICAL on every architecture (unlike struct stat,
    // whose field order differs between x86_64 and arm64 and whose libc
    // symbol is version-scripted). macOS uses lstat(2) against the one
    // 64-bit-inode layout Apple ships on arm64 — the only Apple architecture
    // this project runs on (dev laptops and CI are Apple Silicon; production
    // is Linux).
    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int statx(int dirFd, string path, int flags, uint mask, Span<byte> buffer);

    [LibraryImport("libc", EntryPoint = "lstat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int lstat_macos(string path, Span<byte> buffer);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int fsync(int fd);

    [LibraryImport("libc")]
    private static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr realpath(string path, IntPtr resolvedName);

    [LibraryImport("libc")]
    private static partial void free(IntPtr ptr);

    /// <summary>
    /// Open (creating if needed, mode 0600) a lock file and return its
    /// handle, bypassing FileStream entirely: the .NET Unix runtime emulates
    /// FileShare with flock(2) locks of its own during open, which contend
    /// with a real lock holder and turn "wait for the lock" into an
    /// IOException at open time. Lock files are flock'd and never read or
    /// written, so raw open(2) is the honest primitive.
    /// </summary>
    internal static SafeFileHandle OpenLockFile(string path)
    {
        const int O_RDWR = 0x2; // same on Linux and macOS
        const int ENOENT = 2;
        int O_CLOEXEC = OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;

        for (var attempt = 0; ; attempt++)
        {
            var fd = open(path, O_RDWR | O_CLOEXEC);
            if (fd >= 0)
                return new SafeFileHandle(fd, ownsHandle: true);
            var errno = Marshal.GetLastPInvokeError();
            if (errno == EINTR)
                continue;
            if (errno == ENOENT && attempt < 100)
            {
                // Create with creat(2), then loop to reopen. NOT the 3-arg
                // open(2): its mode parameter rides the variadic slot, and
                // Apple's arm64 ABI passes variadics on the stack — a fixed
                // 3-arg P/Invoke sends mode via register and the file is
                // created with garbage permissions. creat is non-variadic.
                var created = creat(path, 0x180 /* 0600 */);
                if (created >= 0)
                    _ = close(created);
                continue; // a concurrent creator racing us is fine — reopen whatever exists now
            }
            throw new KnapperException(VaultErrorCode.IoError, $"open({path}) failed (errno {errno})");
        }
    }

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int creat(string path, int mode);

    /// <summary>
    /// Try to take an advisory lock without blocking. True = acquired,
    /// false = held elsewhere; anything else throws.
    /// </summary>
    internal static bool TryFlock(SafeFileHandle handle, int operation)
    {
        while (true)
        {
            if (flock(handle, operation | LOCK_NB) == 0)
                return true;
            var errno = Marshal.GetLastPInvokeError();
            if (Array.IndexOf(WouldBlock, errno) >= 0)
                return false;
            if (errno == EINTR)
                continue;
            throw new KnapperException(VaultErrorCode.IoError, $"flock failed (errno {errno})");
        }
    }

    internal static void Unflock(SafeFileHandle handle)
    {
        // Best-effort: closing the descriptor releases the lock anyway.
        _ = flock(handle, LOCK_UN);
    }

    /// <summary>
    /// Atomic no-clobber commit: hard-link <paramref name="existingPath"/> to
    /// <paramref name="newPath"/>. Unlike exists-then-rename, this cannot
    /// replace a file that appears concurrently — the kernel refuses.
    /// </summary>
    internal static void Link(string existingPath, string newPath)
    {
        if (link(existingPath, newPath) == 0)
            return;
        var errno = Marshal.GetLastPInvokeError();
        throw errno == EEXIST
            ? new KnapperException(VaultErrorCode.AlreadyExists, $"file already exists: {newPath}")
            : new KnapperException(VaultErrorCode.IoError, $"link({existingPath}, {newPath}) failed (errno {errno})");
    }

    /// <summary>
    /// ATOMIC CAPTURE of a pathname: move whatever <paramref name="existingPath"/>
    /// names to <paramref name="newPath"/> in one syscall.
    ///
    /// <para>This is the primitive that lets move and delete stop deleting.
    /// Every content check expires the instant it returns, so a
    /// check-then-<c>unlink</c> can always remove a replacement that landed
    /// in between — and POSIX offers no inode-conditional unlink to close
    /// that. rename(2) sidesteps the question: it TAKES the pathname
    /// whatever it currently holds, and the decision about what may be
    /// destroyed is made afterwards, against a private name no other writer
    /// knows. If what we captured turns out to be somebody else's, it is
    /// linked back.</para>
    ///
    /// <para>Callers pass a fresh hidden temp as <paramref name="newPath"/>:
    /// rename(2) silently replaces an existing destination, so a name that
    /// cannot already exist is what makes that safe. NEVER hand this a
    /// pathname an agent or Sync could hold — use <see cref="Link"/> for any
    /// commit that must not clobber.</para>
    /// </summary>
    internal static void Rename(string existingPath, string newPath)
    {
        if (rename(existingPath, newPath) == 0)
            return;
        var errno = Marshal.GetLastPInvokeError();
        const int ENOENT = 2;
        throw errno == ENOENT
            ? new KnapperException(VaultErrorCode.NotFound, $"no such file: {existingPath}")
            : new KnapperException(VaultErrorCode.IoError,
                $"rename({existingPath}, {newPath}) failed (errno {errno})");
    }

    /// <summary>
    /// Hard-link WITHOUT following a final-component symlink: linkat(2) with
    /// flags 0 on both platforms. Plain link(2) diverges exactly where it is
    /// most dangerous — macOS FOLLOWS a symlink source (so linking a note that
    /// was just swapped for a symlink hard-links the OUT-OF-VAULT target into
    /// the vault, reproduced 2026-08-20), while Linux links the symlink inode
    /// itself (publishing a symlink into a vault that bans them). With
    /// linkat(…, 0) both platforms link the symlink itself, so whatever the
    /// final component was at the instant of the call is captured AS-IS under
    /// the new name — and a caller who owns that new name can then inspect it
    /// with non-following metadata and reject. Directory symlinks in the PATH
    /// are still followed (flags govern only the final component); the
    /// containment proofs cover those.
    /// </summary>
    internal static void LinkNoFollow(string existingPath, string newPath)
    {
        var atFdCwd = OperatingSystem.IsMacOS() ? -2 : -100;
        if (linkat(atFdCwd, existingPath, atFdCwd, newPath, 0) == 0)
            return;
        var errno = Marshal.GetLastPInvokeError();
        const int ENOENT = 2;
        throw errno switch
        {
            EEXIST => new KnapperException(VaultErrorCode.AlreadyExists, $"file already exists: {newPath}"),
            ENOENT => new KnapperException(VaultErrorCode.NotFound, $"no such file: {existingPath}"),
            _ => new KnapperException(VaultErrorCode.IoError,
                $"linkat({existingPath}, {newPath}) failed (errno {errno})"),
        };
    }

    /// <summary>
    /// ATOMIC EXCHANGE of two pathnames: after one syscall each name holds
    /// what the other held, and at no instant does either name hold nothing.
    ///
    /// <para>This is to replace what <see cref="Rename"/> is to move/delete:
    /// the primitive that lets a commit stop deciding on the strength of an
    /// expired check. A plain overwriting rename destroys whatever the target
    /// holds NOW — which a moment earlier passed a SHA check that has already
    /// expired — so an external replacement landing in that gap was silently
    /// lost. The exchange TAKES the target's current content to a private
    /// name instead, and the decision about what may be discarded is made
    /// afterwards, against bytes no other writer can touch.</para>
    ///
    /// <para>Both paths must exist (the kernel refuses otherwise — a target
    /// deleted externally fails here rather than being resurrected), and the
    /// filesystem must support the swap. ext4 (production CT), APFS (dev),
    /// tmpfs, btrfs and xfs all do; a filesystem that does not fails LOUDLY
    /// with the errno — never fall back to an overwriting rename, which is
    /// the exact defect this primitive exists to remove.</para>
    /// </summary>
    internal static void Exchange(string pathA, string pathB)
    {
        const uint RENAME_EXCHANGE = 2; // Linux renameat2(2)
        const uint RENAME_SWAP = 2;     // macOS renamex_np(2)
        const int AT_FDCWD = -100;      // Linux only; macOS takes no dirfd
        var rc = OperatingSystem.IsMacOS()
            ? renamex_np(pathA, pathB, RENAME_SWAP)
            : renameat2(AT_FDCWD, pathA, AT_FDCWD, pathB, RENAME_EXCHANGE);
        if (rc == 0)
            return;
        var errno = Marshal.GetLastPInvokeError();
        const int ENOENT = 2;
        throw errno == ENOENT
            ? new KnapperException(VaultErrorCode.NotFound,
                $"exchange({pathA}, {pathB}): one of the paths no longer exists (errno {errno})")
            : new KnapperException(VaultErrorCode.IoError,
                $"exchange({pathA}, {pathB}) failed (errno {errno}) — if this filesystem cannot atomically " +
                "swap two names (renameat2 RENAME_EXCHANGE / renamex_np RENAME_SWAP), it cannot host this vault");
    }

    /// <summary>
    /// fsync the directory so a rename/link survives a crash. Best-effort:
    /// some filesystems refuse directory fsync; that is non-fatal (same call
    /// is best-effort in the reference implementation).
    /// </summary>
    internal static void FsyncDirectory(string directory)
    {
        const int O_RDONLY = 0;
        var fd = open(directory, O_RDONLY);
        if (fd < 0)
            return;
        try
        {
            _ = fsync(fd);
        }
        finally
        {
            _ = close(fd);
        }
    }

    /// <summary>
    /// A file's non-following metadata: stable identity (device + inode),
    /// type, permissions, size, and mtime.
    ///
    /// <para>Identity exists because BYTE EQUALITY IS NOT OWNERSHIP: whether
    /// a private temp may be deleted is decided by whether it is still the
    /// inode Knapper created, never by whether its bytes happen to match
    /// (review round three — a byte-identical third-party write was deleted
    /// on the strength of a content comparison). The inverse rule is just as
    /// firm: METADATA EQUALITY IS NOT CONTENT EQUALITY. Size and mtime are
    /// parsed (their offsets layout-pinned by PosixStatTests) but must never
    /// serve as a content precondition — an in-place write can preserve
    /// both (mtime is user-settable and granularity-aliased), and ctime
    /// cannot rescue such a stamp because rename(2)/exchange bump it on the
    /// files they move, so no stat tuple can ever prove bytes unchanged
    /// (round four — a size+mtime fast path in the authorized-base
    /// judgement silently destroyed a raced in-place write). Content is
    /// proved by hashing bytes, nothing less.</para>
    /// </summary>
    internal readonly record struct StatInfo(
        ulong Dev, ulong Ino, uint Mode, long Size, long MtimeSec, long MtimeNsec)
    {
        private const uint S_IFMT = 0xF000;
        private const uint S_IFREG = 0x8000;
        private const uint S_IFLNK = 0xA000;

        internal bool IsRegular => (Mode & S_IFMT) == S_IFREG;
        internal bool IsSymlink => (Mode & S_IFMT) == S_IFLNK;
        internal UnixFileMode Permissions => (UnixFileMode)(Mode & 0xFFF);

        /// <summary>Same physical file: device and inode. The cleanup/ownership question — and ONLY that; it says nothing about content.</summary>
        internal bool SameIdentity(StatInfo other) => Dev == other.Dev && Ino == other.Ino;
    }

    /// <summary>
    /// lstat(2)/statx(2): the entry itself, never what a symlink points at.
    /// Throws <see cref="VaultErrorCode.NotFound"/> when the path does not
    /// exist, <see cref="VaultErrorCode.IoError"/> otherwise.
    /// </summary>
    internal static StatInfo LStat(string path)
    {
        Span<byte> buffer = stackalloc byte[256];
        int rc;
        if (OperatingSystem.IsMacOS())
        {
            rc = lstat_macos(path, buffer);
        }
        else
        {
            const int AT_FDCWD = -100;
            const int AT_SYMLINK_NOFOLLOW = 0x100;
            const uint STATX_BASIC_STATS = 0x7ff;
            rc = statx(AT_FDCWD, path, AT_SYMLINK_NOFOLLOW, STATX_BASIC_STATS, buffer);
        }
        if (rc != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            const int ENOENT = 2;
            throw errno == ENOENT
                ? new KnapperException(VaultErrorCode.NotFound, $"no such file: {path}")
                : new KnapperException(VaultErrorCode.IoError, $"lstat({path}) failed (errno {errno})");
        }

        if (OperatingSystem.IsMacOS())
        {
            // __DARWIN_STRUCT_STAT64: dev@0(i32) mode@4(u16) nlink@6(u16)
            // ino@8(u64) uid@16 gid@20 rdev@24 atime@32 mtime@48 ctime@64
            // (one more timespec @80, unused here) size@96 — timespecs are
            // two 8-byte longs.
            return new StatInfo(
                Dev: Read<uint>(buffer, 0),
                Ino: Read<ulong>(buffer, 8),
                Mode: Read<ushort>(buffer, 4),
                Size: Read<long>(buffer, 96),
                MtimeSec: Read<long>(buffer, 48),
                MtimeNsec: Read<long>(buffer, 56));
        }
        // struct statx (arch-independent): mask@0 blksize@4 attributes@8
        // nlink@16 uid@20 gid@24 mode@28(u16) ino@32(u64) size@40(u64)
        // blocks@48 attributes_mask@56 atime@64 btime@80 ctime@96 mtime@112
        // (timestamps: i64 sec, u32 nsec) rdev_major@128 rdev_minor@132
        // dev_major@136 dev_minor@140.
        return new StatInfo(
            Dev: ((ulong)Read<uint>(buffer, 136) << 32) | Read<uint>(buffer, 140),
            Ino: Read<ulong>(buffer, 32),
            Mode: Read<ushort>(buffer, 28),
            Size: (long)Read<ulong>(buffer, 40),
            MtimeSec: Read<long>(buffer, 112),
            MtimeNsec: Read<uint>(buffer, 120));

        static T Read<T>(ReadOnlySpan<byte> buffer, int offset) where T : struct =>
            System.Runtime.InteropServices.MemoryMarshal.Read<T>(buffer[offset..]);
    }

    /// <summary>Canonical absolute path with every symlink resolved. Throws if the path does not exist.</summary>
    internal static string RealPath(string path)
    {
        var ptr = realpath(path, IntPtr.Zero);
        if (ptr == IntPtr.Zero)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw new KnapperException(VaultErrorCode.IoError, $"realpath({path}) failed (errno {errno})");
        }
        try
        {
            return Marshal.PtrToStringUTF8(ptr)
                ?? throw new KnapperException(VaultErrorCode.IoError, $"realpath({path}) returned an unreadable string");
        }
        finally
        {
            free(ptr);
        }
    }
}
