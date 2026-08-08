using Knapper.Core.Interop;
using Knapper.Core.Vault;

namespace Knapper.Core.Locking;

/// <summary>
/// Cross-PROCESS advisory locks over flock(2) — not an asyncio/in-memory
/// affair, per the brief: the lock must hold across every worker process,
/// and the git commit job is a separate process by design.
///
/// <para>Two lock kinds, one ordering rule:</para>
/// <list type="bullet">
/// <item><b>Path lock</b> (every mutation): the vault-wide commit lock
/// SHARED, then the per-path lock EXCLUSIVE. Shared commit acquisition means
/// mutations on different paths run concurrently.</item>
/// <item><b>Commit lock</b> (the git commit job): the vault-wide lock
/// EXCLUSIVE — so a snapshot can never see a prepared-but-unverified write,
/// and no mutation starts while a snapshot is in flight.</item>
/// </list>
/// <para>Global-before-path, always, and the commit job takes no path locks —
/// so the lock graph has no cycle and deadlock is structurally impossible.
/// flock is per open-file-description, so two acquisitions in ONE process
/// exclude each other too; in-process callers get real mutual exclusion
/// without a separate semaphore layer.</para>
///
/// <para>Lock files live outside the vault (they must never sync) in a 0700
/// directory, named by SHA-256 of the normalized relative path — path
/// casing/length never leaks into filesystem limits.</para>
/// </summary>
public sealed class VaultLockManager
{
    private const string CommitLockName = "commit.lock";
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(25);

    private readonly string _lockDirectory;

    public VaultLockManager(string lockDirectory)
    {
        if (string.IsNullOrWhiteSpace(lockDirectory))
            throw new KnapperException(VaultErrorCode.IoError, "lock directory is not configured");
        Directory.CreateDirectory(lockDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (File.ResolveLinkTarget(lockDirectory, returnFinalTarget: false) is not null)
            throw new KnapperException(VaultErrorCode.SymlinkRejected,
                $"lock directory must not be a symlink: {lockDirectory}");
        _lockDirectory = lockDirectory;
    }

    /// <summary>
    /// Acquire the mutation lock for one path: vault-wide shared + per-path
    /// exclusive. Dispose to release. Throws <see cref="VaultErrorCode.LockTimeout"/>
    /// past the deadline, holding nothing.
    /// </summary>
    public IDisposable AcquirePathLock(VaultPath path, TimeSpan timeout)
    {
        var deadline = Deadline(timeout);
        var global = Acquire(CommitLockName, Posix.LOCK_SH, deadline,
            "vault-wide lock (is a git commit snapshot in progress?)");
        try
        {
            var perPath = Acquire(LockFileName(path.Relative), Posix.LOCK_EX, deadline,
                $"path lock for '{path.Relative}'");
            return new CompositeLock(perPath, global);
        }
        catch
        {
            global.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Acquire the vault-wide lock exclusively (the git commit job). Waits for
    /// in-flight mutations to drain; blocks new ones from starting.
    /// </summary>
    public IDisposable AcquireCommitLock(TimeSpan timeout) =>
        Acquire(CommitLockName, Posix.LOCK_EX, Deadline(timeout),
            "vault-wide commit lock (are mutations in flight?)");

    internal static string LockFileName(string relativePath) =>
        VaultHash.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(relativePath)) + ".lock";

    private static long Deadline(TimeSpan timeout) =>
        Environment.TickCount64 + (long)timeout.TotalMilliseconds;

    private HeldLock Acquire(string lockFileName, int operation, long deadline, string what)
    {
        // Raw open(2), not FileStream: the runtime's flock-based FileShare
        // emulation contends with a real lock holder AT OPEN TIME, turning
        // "wait for the lock" into an immediate IOException. See Posix.OpenLockFile.
        var handle = Posix.OpenLockFile(Path.Combine(_lockDirectory, lockFileName));
        try
        {
            while (!Posix.TryFlock(handle, operation))
            {
                if (Environment.TickCount64 >= deadline)
                {
                    throw new KnapperException(VaultErrorCode.LockTimeout,
                        $"timed out acquiring {what}");
                }
                Thread.Sleep(RetryInterval);
            }
            var held = new HeldLock(handle);
            handle = null!;
            return held;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private sealed class HeldLock(Microsoft.Win32.SafeHandles.SafeFileHandle handle) : IDisposable
    {
        private Microsoft.Win32.SafeHandles.SafeFileHandle? _handle = handle;

        public void Dispose()
        {
            var h = Interlocked.Exchange(ref _handle, null);
            if (h is null)
                return;
            Posix.Unflock(h);
            h.Dispose();
        }
    }

    private sealed class CompositeLock(IDisposable perPath, IDisposable global) : IDisposable
    {
        public void Dispose()
        {
            // Reverse acquisition order.
            perPath.Dispose();
            global.Dispose();
        }
    }
}
