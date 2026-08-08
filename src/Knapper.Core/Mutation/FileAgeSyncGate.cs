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
    public void AssertMutationsAllowed()
    {
        var age = HeartbeatAgeSeconds();
        if (age is null)
        {
            throw new KnapperException(VaultErrorCode.MutationBlocked,
                $"sync heartbeat file is missing ({options.HeartbeatPath}) — the sync service looks dead; " +
                "mutations are blocked (fail closed; there is no local fallback)");
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
        catch (IOException)
        {
            return null;
        }
    }
}
