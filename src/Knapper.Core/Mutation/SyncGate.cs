namespace Knapper.Core.Mutation;

/// <summary>
/// The sync-health gate (brief §8): mutations require a healthy continuous-
/// sync service. The real implementation (reading `ob sync-status` /
/// heartbeat age) arrives with the MCP host; Core only defines the seam the
/// mutation service fails closed through.
/// </summary>
public interface ISyncGate
{
    /// <summary>Throws <see cref="VaultErrorCode.MutationBlocked"/> when mutations must not proceed.</summary>
    void AssertMutationsAllowed();
}

/// <summary>Static gate: always-open for dev/tests, always-closed to exercise fail-closed paths.</summary>
public sealed class StaticSyncGate(bool healthy, string? reason = null) : ISyncGate
{
    public static readonly StaticSyncGate Open = new(true);

    public void AssertMutationsAllowed()
    {
        if (!healthy)
        {
            throw new KnapperException(VaultErrorCode.MutationBlocked,
                reason ?? "sync is unhealthy — mutations are blocked (fail closed; there is no local fallback)");
        }
    }
}

/// <summary>
/// The ONE mapping from <see cref="Options.SyncOptions"/> to a gate, shared by
/// the server's startup and <c>knapper doctor</c>. They each spelled it once:
/// the server lower-cased <c>Sync:Mode</c> and refused anything unknown,
/// while doctor compared case-sensitively and treated everything else as
/// "open" — so <c>Sync__Mode=Heartbeat</c> ran gated in the service and read
/// "sync gate is OPEN" in doctor, and a typo the server refuses to boot on
/// passed doctor with a warning (security review 2026-11-24).
/// </summary>
public static class SyncGates
{
    /// <summary>Throws <see cref="InvalidOperationException"/> for a configuration the server refuses to start on.</summary>
    public static ISyncGate Create(Options.SyncOptions sync) =>
        (sync.Mode ?? "").ToLowerInvariant() switch
        {
            "open" => StaticSyncGate.Open,
            "heartbeat" => string.IsNullOrWhiteSpace(sync.HeartbeatPath)
                ? throw new InvalidOperationException("Sync:Mode is 'heartbeat' but Sync:HeartbeatPath is empty.")
                : new FileAgeSyncGate(sync),
            _ => throw new InvalidOperationException(
                $"Sync:Mode must be 'open' or 'heartbeat', got '{sync.Mode}'."),
        };
}
