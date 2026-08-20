using Knapper.Core.Mutation;

namespace Knapper.Core.Tests.Mutation;

/// <summary>
/// The conflict and sync gates run before the locks AND again with them
/// held. A mutation can wait up to Vault:LockTimeoutMs for a lock, and a
/// batch adds its whole validate phase on top — long enough for Sync to
/// materialize a conflict sibling, or for the heartbeat to cross its maximum
/// age, while a write sits in the queue holding an answer from before it got
/// there.
///
/// <para>This narrows a window rather than closing one: the locks bind
/// cooperating Knapper processes only, so Sync can still land a conflict file
/// the instant after any check. What these pin is that a write acts on a gate
/// result taken after the waiting rather than before it — and that a stale
/// gate leaves the vault untouched, not half-written.</para>
/// </summary>
public sealed class GateRecheckTests : IDisposable
{
    private readonly MutationVault _v = new();

    public void Dispose() => _v.Dispose();

    /// <summary>Healthy on the pre-lock call, unhealthy by the time the locks are held.</summary>
    private sealed class GoesStaleSyncGate : ISyncGate
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void AssertMutationsAllowed()
        {
            if (Interlocked.Increment(ref _calls) > 1)
            {
                throw new KnapperException(VaultErrorCode.MutationBlocked,
                    "the heartbeat went stale while this mutation waited for its lock");
            }
        }
    }

    private VaultMutationService Staleing(out GoesStaleSyncGate gate)
    {
        gate = new GoesStaleSyncGate();
        return _v.ServiceWithSyncGate(gate);
    }

    [Fact]
    public void An_edit_whose_sync_gate_goes_stale_under_the_lock_writes_nothing()
    {
        var sha = _v.Write("Notes/a.md", "base\n");
        var service = Staleing(out var gate);

        Should.Throw<KnapperException>(() => service.Append("Notes/a.md", sha, "more\n"))
            .Code.ShouldBe(VaultErrorCode.MutationBlocked);

        gate.Calls.ShouldBe(2, "the gate must be asserted again with the lock held");
        _v.ReadText("Notes/a.md").ShouldBe("base\n");
    }

    [Fact]
    public void A_create_whose_sync_gate_goes_stale_under_the_lock_creates_nothing()
    {
        var service = Staleing(out var gate);

        Should.Throw<KnapperException>(() => service.Create("Notes/new.md", "hello\n"))
            .Code.ShouldBe(VaultErrorCode.MutationBlocked);

        gate.Calls.ShouldBe(2);
        File.Exists(_v.Absolute("Notes/new.md")).ShouldBeFalse();
    }

    [Fact]
    public void A_move_whose_sync_gate_goes_stale_under_the_locks_moves_nothing()
    {
        var sha = _v.Write("Notes/a.md", "base\n");
        var service = Staleing(out var gate);

        Should.Throw<KnapperException>(() => service.Move("Notes/a.md", "Notes/b.md", sha))
            .Code.ShouldBe(VaultErrorCode.MutationBlocked);

        gate.Calls.ShouldBe(2);
        _v.ReadText("Notes/a.md").ShouldBe("base\n");
        File.Exists(_v.Absolute("Notes/b.md")).ShouldBeFalse();
    }

    [Fact]
    public void A_delete_whose_sync_gate_goes_stale_under_the_lock_deletes_nothing()
    {
        var sha = _v.Write("Notes/a.md", "base\n");
        var service = Staleing(out var gate);

        Should.Throw<KnapperException>(() => service.Delete("Notes/a.md", sha))
            .Code.ShouldBe(VaultErrorCode.MutationBlocked);

        gate.Calls.ShouldBe(2);
        _v.ReadText("Notes/a.md").ShouldBe("base\n");
        Directory.Exists(_v.Absolute(".trash")).ShouldBeFalse();
    }

    /// <summary>
    /// Batch re-asserts after VALIDATE, not just after the locks: validating
    /// every item is the long pole, and it is the last moment at which
    /// nothing has been written.
    /// </summary>
    [Fact]
    public void A_batch_whose_sync_gate_goes_stale_before_the_first_apply_writes_nothing()
    {
        var a = _v.Write("Notes/a.md", "a\n");
        var b = _v.Write("Notes/b.md", "b\n");
        var service = Staleing(out var gate);

        Should.Throw<KnapperException>(() => service.Batch([
            new BatchItem(BatchItemKind.Append, "Notes/a.md", ExpectSha256: a, Text: "more\n"),
            new BatchItem(BatchItemKind.Append, "Notes/b.md", ExpectSha256: b, Text: "more\n"),
        ])).Code.ShouldBe(VaultErrorCode.MutationBlocked);

        gate.Calls.ShouldBe(2);
        _v.ReadText("Notes/a.md").ShouldBe("a\n");
        _v.ReadText("Notes/b.md").ShouldBe("b\n");
    }

    /// <summary>Signals once the pre-lock gates have run and passed.</summary>
    private sealed class SignallingSyncGate(ManualResetEventSlim signal) : ISyncGate
    {
        public void AssertMutationsAllowed() => signal.Set();
    }

    /// <summary>
    /// The realistic shape: the mutation's own pre-lock checks pass, it
    /// blocks on a lock another holder has, and Sync materializes a conflict
    /// sibling while it waits. Holding the lock for the whole window is what
    /// makes this deterministic — the mutation cannot proceed until the
    /// conflict file exists, and the signal proves the pre-lock pass had
    /// already run and found nothing.
    /// </summary>
    [Fact]
    public void A_conflict_that_lands_while_a_mutation_waits_for_the_lock_blocks_the_write()
    {
        var sha = _v.Write("Notes/a.md", "base\n");
        var vp = _v.Resolver.Resolve("Notes/a.md");
        using var preLockPassed = new ManualResetEventSlim();
        var service = _v.ServiceWithSyncGate(new SignallingSyncGate(preLockPassed));

        Task<MutationResult> mutation;
        using (_v.Locks.AcquirePathLock(vp, TimeSpan.FromSeconds(5)))
        {
            mutation = Task.Run(() => service.Append("Notes/a.md", sha, "more\n"));
            preLockPassed.Wait(TimeSpan.FromSeconds(5))
                .ShouldBeTrue("the mutation's pre-lock gates should have run and passed");
            _v.Write("Notes/a (Conflicted copy 2026-08-19 12.00.00).md", "sync's copy\n");
        }

        var ex = Should.Throw<KnapperException>(() => mutation.GetAwaiter().GetResult());
        ex.Code.ShouldBe(VaultErrorCode.MutationBlocked);
        _v.ReadText("Notes/a.md").ShouldBe("base\n", "a conflicted note must not be written");
    }

    [Fact]
    public void A_gate_that_stays_healthy_still_lets_the_mutation_through()
    {
        var sha = _v.Write("Notes/a.md", "base\n");
        _v.Service.Append("Notes/a.md", sha, "more\n");
        _v.ReadText("Notes/a.md").ShouldBe("base\nmore\n");
    }
}
