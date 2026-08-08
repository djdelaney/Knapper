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
