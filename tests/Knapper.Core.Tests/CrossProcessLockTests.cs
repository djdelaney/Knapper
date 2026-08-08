using System.Diagnostics;
using Knapper.Core.Locking;
using Knapper.Core.Vault;

namespace Knapper.Core.Tests;

/// <summary>
/// GENUINE two-process lock tests: a child process (Knapper.LockProbe,
/// copied into this test's output directory by project reference) takes
/// locks for real while this process races it. The brief forbids trusting
/// the lock design until it passes exactly this shape of test.
/// </summary>
public sealed class CrossProcessLockTests : IDisposable
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan Ample = TimeSpan.FromSeconds(10);

    private readonly TempDir _dir = new();
    private readonly string _lockDir;
    private readonly VaultLockManager _manager;

    public CrossProcessLockTests()
    {
        _lockDir = Path.Combine(_dir.Path, "locks");
        _manager = new VaultLockManager(_lockDir);
    }

    public void Dispose() => _dir.Dispose();

    private static VaultPath P(string relative) => new() { Relative = relative, Absolute = "/" + relative };

    /// <summary>Spawn the probe and block until it prints ACQUIRED.</summary>
    private Process SpawnHolding(string kind, string relativePath, int holdMs)
    {
        var probeDll = Path.Combine(AppContext.BaseDirectory, "Knapper.LockProbe.dll");
        File.Exists(probeDll).ShouldBeTrue($"probe not found at {probeDll}");

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { "exec", probeDll, _lockDir, kind, relativePath, holdMs.ToString(), "5000" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;

        var line = process.StandardOutput.ReadLine();
        if (line != "ACQUIRED")
        {
            var stderr = process.StandardError.ReadToEnd();
            process.Kill(entireProcessTree: true);
            Assert.Fail($"probe did not acquire: got '{line}', stderr: {stderr}");
        }
        return process;
    }

    private static void AwaitExit(Process process)
    {
        process.WaitForExit(15_000).ShouldBeTrue("probe never exited");
        process.ExitCode.ShouldBe(0);
        process.Dispose();
    }

    [Fact]
    public void A_path_lock_held_by_another_process_excludes_us_until_released()
    {
        var probe = SpawnHolding("path", "Notes/Daily.md", holdMs: 1500);
        try
        {
            Should.Throw<KnapperException>(() => _manager.AcquirePathLock(P("Notes/Daily.md"), Short))
                .Code.ShouldBe(VaultErrorCode.LockTimeout);

            // ...and becomes available once the probe lets go.
            using var after = _manager.AcquirePathLock(P("Notes/Daily.md"), Ample);
        }
        finally
        {
            AwaitExit(probe);
        }
    }

    [Fact]
    public void Another_process_holding_a_different_path_does_not_exclude_us()
    {
        var probe = SpawnHolding("path", "Notes/Other.md", holdMs: 1000);
        try
        {
            using var held = _manager.AcquirePathLock(P("Notes/Mine.md"), Short);
        }
        finally
        {
            AwaitExit(probe);
        }
    }

    [Fact]
    public void A_commit_snapshot_in_another_process_blocks_all_mutations()
    {
        var probe = SpawnHolding("commit", "-", holdMs: 1500);
        try
        {
            Should.Throw<KnapperException>(() => _manager.AcquirePathLock(P("any.md"), Short))
                .Code.ShouldBe(VaultErrorCode.LockTimeout);

            using var after = _manager.AcquirePathLock(P("any.md"), Ample);
        }
        finally
        {
            AwaitExit(probe);
        }
    }

    [Fact]
    public void A_mutation_in_another_process_blocks_the_commit_snapshot()
    {
        var probe = SpawnHolding("path", "busy.md", holdMs: 1500);
        try
        {
            Should.Throw<KnapperException>(() => _manager.AcquireCommitLock(Short))
                .Code.ShouldBe(VaultErrorCode.LockTimeout);

            using var after = _manager.AcquireCommitLock(Ample);
        }
        finally
        {
            AwaitExit(probe);
        }
    }

    [Fact]
    public void A_dead_lock_holder_does_not_wedge_the_vault()
    {
        // flock releases on process death — the reason it was chosen over
        // lock FILES whose stale presence outlives a crash.
        var probe = SpawnHolding("path", "crash.md", holdMs: 30_000);
        probe.Kill(entireProcessTree: true);
        probe.WaitForExit();
        probe.Dispose();

        using var after = _manager.AcquirePathLock(P("crash.md"), Ample);
    }
}
