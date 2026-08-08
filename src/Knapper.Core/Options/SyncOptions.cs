namespace Knapper.Core.Options;

/// <summary>
/// The sync-health gate's configuration (brief §8: mutations require a
/// healthy continuous-sync service).
/// </summary>
public sealed class SyncOptions
{
    public const string SectionName = "Sync";

    /// <summary>
    /// "heartbeat" (production): mutations require <see cref="HeartbeatPath"/>
    /// to be fresher than <see cref="MaxAgeSeconds"/> — the obsidian-headless
    /// unit's watchdog touches it while `ob sync --continuous` is healthy.
    /// "open" (dev/tests only): no gate; the server logs a warning at startup.
    /// </summary>
    public string Mode { get; set; } = "open";

    public string HeartbeatPath { get; set; } = "";

    public int MaxAgeSeconds { get; set; } = 300;
}
