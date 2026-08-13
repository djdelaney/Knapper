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

    /// <summary>
    /// How stale <see cref="HeartbeatPath"/> may be before mutations fail
    /// closed.
    ///
    /// ⚠️ COUPLED to the probe's tick. This default is sized against
    /// `knapper-heartbeat.timer` firing every 60s — which is only true because
    /// that unit pins `AccuracySec=1s`; systemd's 1min default let firings slip
    /// to 116s on CT 106. Total exposure to an outage is roughly ob's own ~57s
    /// detection latency + the inter-tick gap + this number, so changing the
    /// timer's period without revisiting this silently moves the budget. If the
    /// total is what matters, THIS is the lever to turn — the tick is bounded
    /// below by ob's detection latency and cannot buy much.
    /// </summary>
    public int MaxAgeSeconds { get; set; } = 300;

    /// <summary>
    /// The largest file Obsidian Sync will carry. A write producing more than
    /// this is refused with <see cref="VaultErrorCode.TooLargeToSync"/> rather
    /// than landing a note that verifies locally, commits to git, reports
    /// success, and never reaches a single device.
    ///
    /// ⚠️ This is a property of the SYNC PLAN, not of Knapper — that is why it
    /// is configurable rather than a constant, and why raising it is a
    /// deployment decision. Set it to match your plan's per-file ceiling.
    ///
    /// The default is DELIBERATELY CONSERVATIVE. `ob` reports "max 5.00 MB",
    /// which is ambiguous between 5,000,000 and 5,242,880, and nobody has
    /// bisected the real boundary (CT 106, 2026-08-13). The two errors are not
    /// symmetric: set too low, some writes that would have synced are refused,
    /// loudly and with a typed error naming the limit; set too high, files in
    /// the gap pass the guard and are stranded silently — reproducing the exact
    /// failure the guard exists to prevent, now with a false sense of coverage.
    /// Do not raise this to 5 * 1024 * 1024 without measuring first.
    ///
    /// Applies in every <see cref="Mode"/>, including "open": a guard with a
    /// mode-shaped hole in it is a bypass. A test or dev vault needing larger
    /// files raises the number explicitly, where it is visible.
    /// </summary>
    public long MaxFileBytes { get; set; } = 5_000_000;
}
