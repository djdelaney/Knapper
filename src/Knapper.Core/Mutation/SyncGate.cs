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
