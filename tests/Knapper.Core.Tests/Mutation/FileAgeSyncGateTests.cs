using Knapper.Core.Mutation;
using Knapper.Core.Options;

namespace Knapper.Core.Tests.Mutation;

/// <summary>
/// The heartbeat gate must fail closed in BOTH directions. Staleness was
/// always covered; a heartbeat mtime in the FUTURE — a clock stepped
/// backward, a container restored from a snapshot, a bad touch — used to
/// read as "fresh" for the whole skew, keeping mutations enabled for hours
/// while the sync service could be dead the entire time. A timestamp that
/// cannot be evidence of a live watchdog must block, same as a missing one.
/// </summary>
public sealed class FileAgeSyncGateTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _heartbeat;

    public FileAgeSyncGateTests() => _heartbeat = Path.Combine(_dir.Path, "heartbeat");

    public void Dispose() => _dir.Dispose();

    private FileAgeSyncGate Gate(int maxAgeSeconds = 300) =>
        new(new SyncOptions { HeartbeatPath = _heartbeat, MaxAgeSeconds = maxAgeSeconds });

    private void TouchAt(double secondsFromNow)
    {
        File.WriteAllText(_heartbeat, "");
        File.SetLastWriteTimeUtc(_heartbeat, DateTime.UtcNow.AddSeconds(secondsFromNow));
    }

    [Fact]
    public void A_missing_heartbeat_blocks_mutations()
    {
        Should.Throw<KnapperException>(() => Gate().AssertMutationsAllowed())
            .Code.ShouldBe(VaultErrorCode.MutationBlocked);
    }

    [Fact]
    public void A_fresh_heartbeat_allows_mutations()
    {
        TouchAt(-5);
        Should.NotThrow(() => Gate().AssertMutationsAllowed());
    }

    [Fact]
    public void A_stale_heartbeat_blocks_mutations()
    {
        TouchAt(-400);
        var ex = Should.Throw<KnapperException>(() => Gate(maxAgeSeconds: 300).AssertMutationsAllowed());
        ex.Code.ShouldBe(VaultErrorCode.MutationBlocked);
        ex.Message.ShouldContain("old");
    }

    /// <summary>
    /// Small skew is tolerated: mtime granularity and NTP corrections make
    /// exactly-zero too strict, and refusing every mutation over a 2s skew
    /// would be a self-inflicted outage.
    /// </summary>
    [Fact]
    public void A_slightly_future_heartbeat_within_the_tolerance_allows_mutations()
    {
        // Just inside the boundary. The elapsed time between the touch and
        // the check only shrinks the measured skew, so this cannot flake
        // toward blocking.
        TouchAt(FileAgeSyncGate.FutureToleranceSeconds - 1);
        Should.NotThrow(() => Gate().AssertMutationsAllowed());
    }

    [Fact]
    public void A_future_heartbeat_just_beyond_the_tolerance_blocks_mutations()
    {
        // Just outside the boundary, with margin for the wall-clock time the
        // test itself consumes (elapsed time shrinks the measured skew).
        TouchAt(FileAgeSyncGate.FutureToleranceSeconds + 5);
        var ex = Should.Throw<KnapperException>(() => Gate().AssertMutationsAllowed());
        ex.Code.ShouldBe(VaultErrorCode.MutationBlocked);
        ex.Message.ShouldContain("FUTURE");
    }

    /// <summary>
    /// The review's worst case spelled out: a timestamp further into the
    /// future than MaxAgeSeconds. Under `age > max` alone this was the
    /// LONGEST-lived fail-open — the gate would have read "healthy" until
    /// the wall clock caught up past the skew plus the max age.
    /// </summary>
    [Fact]
    public void A_heartbeat_beyond_max_age_into_the_future_blocks_mutations()
    {
        TouchAt(400);
        Should.Throw<KnapperException>(() => Gate(maxAgeSeconds: 300).AssertMutationsAllowed())
            .Code.ShouldBe(VaultErrorCode.MutationBlocked);
    }

    /// <summary>
    /// The health surface reports the age it measured, sign included — a
    /// negative age is how /health explains WHY mutations are blocked.
    /// </summary>
    [Fact]
    public void The_reported_age_is_negative_for_a_future_heartbeat()
    {
        TouchAt(120);
        Gate().HeartbeatAgeSeconds()!.Value.ShouldBeLessThan(0);
    }

    /// <summary>
    /// The tolerance must never grow past the heartbeat tick (60s): a
    /// withheld touch — the ONLY way Knapper learns sync is unhealthy — must
    /// not be coverable by skew the gate shrugs at.
    /// </summary>
    [Fact]
    public void The_tolerance_is_below_the_heartbeat_tick()
    {
        FileAgeSyncGate.FutureToleranceSeconds.ShouldBeLessThan(60);
    }
}
