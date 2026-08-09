namespace Knapper.Core.Options;

/// <summary>
/// The sync-health gate's configuration (brief §8: mutations require a
/// healthy continuous-sync service).
/// </summary>
public sealed class SyncOptions
{
    public const string SectionName = "Sync";

    /// <summary>
    /// "heartbeat" (production, and the DEFAULT): mutations require
    /// <see cref="HeartbeatPath"/> to be fresher than
    /// <see cref="MaxAgeSeconds"/> — the obsidian-headless unit's watchdog
    /// touches it while `ob sync --continuous` is healthy. "open"
    /// (dev/tests only) is an EXPLICIT opt-out that logs a startup warning.
    /// The default fails closed on purpose: a forgotten env line must refuse
    /// startup (heartbeat with no path), never silently ungate mutations.
    /// </summary>
    public string Mode { get; set; } = "heartbeat";

    public string HeartbeatPath { get; set; } = "";

    public int MaxAgeSeconds { get; set; } = 300;
}
