using Knapper.Core.Options;

namespace Knapper.Core.Mutation;

/// <summary>
/// Production sync gate: a heartbeat file the sync service's watchdog touches
/// while `ob sync --continuous` is healthy. Stale or missing = mutations
/// blocked, fail closed — a missing heartbeat is indistinguishable from a
/// dead sync service, and "couldn't tell" must never resolve to "healthy".
/// Reads stay up either way; only mutations gate on sync health.
/// </summary>
public sealed class FileAgeSyncGate(SyncOptions options) : ISyncGate
{
    /// <summary>
    /// How far into the FUTURE the heartbeat mtime may sit before the gate
    /// fails closed. A future timestamp is not health — it is a clock that
    /// stepped backward, a container restored from a snapshot, or a bad
    /// touch, and under `age > max` alone it read as "fresh" for the whole
    /// skew (hours, after a restore) while the sync service could be dead the
    /// entire time. The tolerance exists only because mtime granularity and
    /// small NTP corrections make exactly-zero too strict; it is deliberately
    /// far below the heartbeat tick (60s), so a withheld touch can never hide
    /// inside it.
    /// </summary>
    internal const double FutureToleranceSeconds = 30;

    public void AssertMutationsAllowed()
    {
        var age = HeartbeatAgeSeconds();
        if (age is null)
        {
            throw new KnapperException(VaultErrorCode.MutationBlocked,
                $"sync heartbeat file is missing ({options.HeartbeatPath}) — the sync service looks dead; " +
                "mutations are blocked (fail closed; there is no local fallback)");
        }
        if (age < -FutureToleranceSeconds)
        {
            throw new KnapperException(VaultErrorCode.MutationBlocked,
                $"sync heartbeat is {-age:F0}s in the FUTURE — the clock stepped or the heartbeat file was " +
                "restored from elsewhere, so the sync watchdog cannot be proven alive; mutations are blocked " +
                "(fail closed) until a fresh touch lands");
        }
        if (age > options.MaxAgeSeconds)
        {
            throw new KnapperException(VaultErrorCode.MutationBlocked,
                $"sync heartbeat is {age:F0}s old (max {options.MaxAgeSeconds}s) — continuous sync looks " +
                "unhealthy; mutations are blocked until it recovers");
        }
    }

    /// <summary>Null when the heartbeat file is missing/unreadable.</summary>
    public double? HeartbeatAgeSeconds()
    {
        try
        {
            var info = new FileInfo(options.HeartbeatPath);
            if (!info.Exists)
                return null;
            return (DateTime.UtcNow - info.LastWriteTimeUtc).TotalSeconds;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Null = "can't tell" = mutations blocked (fail closed) with the
            // typed MutationBlocked shape. A raw UnauthorizedAccessException
            // (or ArgumentException from a blank path) previously escaped as
            // [Internal] — right outcome, wrong code for agents to branch on.
            return null;
        }
    }
}
