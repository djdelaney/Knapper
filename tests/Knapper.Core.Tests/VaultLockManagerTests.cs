using Knapper.Core.Locking;
using Knapper.Core.Vault;

namespace Knapper.Core.Tests;

public sealed class VaultLockManagerTests : IDisposable
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Ample = TimeSpan.FromSeconds(5);

    private readonly TempDir _dir = new();
    private readonly VaultLockManager _manager;

    public VaultLockManagerTests() => _manager = new VaultLockManager(Path.Combine(_dir.Path, "locks"));

    public void Dispose() => _dir.Dispose();

    private static VaultPath P(string relative) => new() { Relative = relative, Absolute = "/" + relative };

    [Fact]
    public void Same_path_excludes_even_within_one_process()
    {
        using var first = _manager.AcquirePathLock(P("a.md"), Ample);
        Should.Throw<KnapperException>(() => _manager.AcquirePathLock(P("a.md"), Short))
            .Code.ShouldBe(VaultErrorCode.LockTimeout);
    }

    [Fact]
    public void Different_paths_do_not_exclude()
    {
        using var first = _manager.AcquirePathLock(P("a.md"), Ample);
        using var second = _manager.AcquirePathLock(P("b.md"), Short); // shared global + distinct path lock
    }

    [Fact]
    public void Release_makes_the_path_available_again()
    {
        _manager.AcquirePathLock(P("a.md"), Ample).Dispose();
        using var again = _manager.AcquirePathLock(P("a.md"), Short);
    }

    [Fact]
    public void Commit_lock_excludes_mutations_and_vice_versa()
    {
        using (var commit = _manager.AcquireCommitLock(Ample))
        {
            Should.Throw<KnapperException>(() => _manager.AcquirePathLock(P("a.md"), Short))
                .Code.ShouldBe(VaultErrorCode.LockTimeout);
        }

        using (var mutation = _manager.AcquirePathLock(P("a.md"), Ample))
        {
            Should.Throw<KnapperException>(() => _manager.AcquireCommitLock(Short))
                .Code.ShouldBe(VaultErrorCode.LockTimeout);
        }
    }

    [Fact]
    public void Failed_path_acquisition_releases_the_global_share()
    {
        using (var blocker = _manager.AcquirePathLock(P("contested.md"), Ample))
        {
            Should.Throw<KnapperException>(() => _manager.AcquirePathLock(P("contested.md"), Short));
        }
        // If the failed acquisition above leaked its global share, this would time out.
        using var commit = _manager.AcquireCommitLock(Short);
    }

    [Fact]
    public void Lock_files_never_land_in_the_vault()
    {
        // Locks are keyed by hash of the relative path — nothing under the
        // lock dir may mirror vault names (Sync must never see them), and
        // hostile path characters must not reach the filesystem.
        VaultLockManager.LockFileName("Notes/Daily.md").ShouldMatch("^[0-9a-f]{64}\\.lock$");
    }

    [Fact]
    public void Refuses_a_symlinked_lock_directory()
    {
        var real = Path.Combine(_dir.Path, "real-locks");
        Directory.CreateDirectory(real);
        var link = Path.Combine(_dir.Path, "linked-locks");
        Directory.CreateSymbolicLink(link, real);

        Should.Throw<KnapperException>(() => new VaultLockManager(link))
            .Code.ShouldBe(VaultErrorCode.SymlinkRejected);
    }
}
