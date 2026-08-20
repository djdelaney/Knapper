using System.Diagnostics;
using Knapper.Core;
using Knapper.Core.Generation;
using Knapper.Core.Mutation;
using Knapper.Core.Options;
using Knapper.Core.Vault;
using Microsoft.Extensions.Options;

namespace Knapper.Mcp;

/// <summary>
/// /health's detailed body (loopback-only by default: it names filesystem
/// paths and conflict files) and /up's boolean-only body for the external
/// monitor. Degraded whenever agent work is impaired: vault unreachable,
/// ripgrep missing, audit log unwritable, sync unhealthy (mutations blocked),
/// or unresolved Sync conflict files (mutations to those notes blocked, and a
/// human needs to know) — and whenever a vault walk could not COMPLETE, since
/// a probe that cannot report is not a probe that reports "fine".
/// </summary>
public sealed class HealthService(
    VaultPathResolver resolver,
    VaultGenerationCounter generation,
    ConflictDetector conflicts,
    ISyncGate syncGate,
    IOptions<VaultOptions> vaultOptions,
    IOptions<SyncOptions> syncOptions,
    ILogger<HealthService> logger)
{
    private string? _ripgrepVersion;
    private long _ripgrepCheckedAt; // Stopwatch timestamp; 0 = never probed

    /// <summary>
    /// Success is cached only briefly — a permanently cached probe leaves
    /// /up healthy forever after rg is removed or broken. Internal so tests
    /// can collapse the TTL; production keeps the default.
    /// </summary>
    internal TimeSpan RipgrepTtl = TimeSpan.FromSeconds(30);

    /// <summary>Probe bound. A hung rg must degrade health, not hang /up.</summary>
    internal int RipgrepTimeoutMs = 5_000;

    /// <summary>
    /// The oversized scan walks the vault, and /up is the monitor's liveness
    /// probe — it must not pay an O(vault) cost per request. Only a SUCCESS
    /// caches: this probe has an "unknown" state like the rg one, and a
    /// cached "could not tell" would keep answering "could not tell" after
    /// the vault became readable again.
    /// </summary>
    internal TimeSpan OversizedTtl = TimeSpan.FromSeconds(60);

    /// <summary>Walk bound. A walk that will not finish must degrade health, not hang /up.</summary>
    internal TimeSpan OversizedBudget = OversizedFiles.DefaultBudget;

    /// <summary>Test seam for the conflict walk's wall clock (see <see cref="OversizedBudget"/>).</summary>
    internal TimeSpan ConflictScanBudget = ConflictDetector.DefaultBudget;

    public sealed record Report(
        string Status,
        string Version,
        VaultInfo Vault,
        SyncInfo Sync,
        RipgrepInfo Ripgrep,
        AuditInfo Audit,
        OversizedInfo Oversized);

    /// <summary>
    /// Files Obsidian Sync will not carry that are PRESENT on this box.
    /// /health only — it names paths. Note the asymmetry: an oversized file
    /// made on a Mac never arrives (measured 2026-08-13), so it is missing
    /// rather than listed, and nothing here can report it.
    ///
    /// <paramref name="Scanned"/> is the state this probe used to lack: false
    /// means the walk did not complete, so <paramref name="Count"/> is 0
    /// because nothing was counted, NOT because nothing is there. Read the two
    /// together or the payload reads as "checked, all clear" at exactly the
    /// moment it means "could not tell".
    ///
    /// <paramref name="ScanError"/> names WHY, because the two causes need
    /// opposite responses and are otherwise indistinguishable: an IO failure
    /// is usually transient and self-clearing, while a budget expiry means
    /// the walk cannot finish in the time allowed and will keep not finishing
    /// until someone acts. /health carries it (it is loopback-only and already
    /// discloses the vault root and conflict filenames); /up never does.
    /// </summary>
    public sealed record OversizedInfo(
        bool Scanned, string? ScanError, int Count, long LimitBytes, IReadOnlyList<string> Files);

    /// <summary>
    /// <paramref name="ConflictScanComplete"/> carries the same distinction for
    /// the conflict walk: false means <paramref name="ConflictFiles"/> is empty
    /// for want of a completed walk. It also used to be the loudest bug on this
    /// path — an unreadable directory anywhere in the vault threw out of
    /// Check() and /health answered 500, breaking its own 200/503 contract.
    /// </summary>
    public sealed record VaultInfo(
        bool Reachable,
        string Root,
        long Generation,
        IReadOnlyList<string> ConflictFiles,
        bool ConflictScanComplete,
        string? ConflictScanError);

    /// <summary>
    /// <paramref name="MutationsAllowed"/> is the SYNC GATE only — whether
    /// continuous sync is healthy enough to accept writes at all. It is not a
    /// promise that every write will succeed: a note with an unresolved Sync
    /// conflict sibling is refused [MutationBlocked] while this reads true,
    /// because the conflict gate is per-file and lives under
    /// <c>vault.conflictFiles</c>. Measured on CT 106, 2026-08-13.
    /// </summary>
    public sealed record SyncInfo(string Mode, bool MutationsAllowed, double? HeartbeatAgeSeconds, string? BlockedReason);

    public sealed record RipgrepInfo(bool Available, string? Version);

    public sealed record AuditInfo(bool Writable, string Path);

    /// <summary>Booleans only — what the external monitor may learn. No paths, no names, no counts.</summary>
    public sealed record UpReport(
        string Status,
        string Version,
        UpBool Vault,
        UpBool Sync,
        UpBool Ripgrep,
        UpBool Audit,
        UpBool Conflicts,
        UpBool Oversized);

    public sealed record UpBool(bool Ok);

    public Report Check()
    {
        var version = BuildInfo.Version;

        var vaultReachable = Directory.Exists(resolver.Root);
        var conflictFiles = vaultReachable ? ScanConflicts() : null;

        bool mutationsAllowed;
        string? blockedReason = null;
        try
        {
            syncGate.AssertMutationsAllowed();
            mutationsAllowed = true;
        }
        catch (KnapperException e)
        {
            mutationsAllowed = false;
            blockedReason = e.Message;
        }
        var heartbeatAge = (syncGate as FileAgeSyncGate)?.HeartbeatAgeSeconds();

        var rgVersion = RipgrepVersion();
        var auditWritable = AuditWritable();
        var oversized = vaultReachable ? Oversized() : null;

        // ── The rule, stated once, because two probes could otherwise drift
        // into an unexplained difference ──────────────────────────────────
        //
        // A FINDING is information about the vault. A conflict file is a
        // finding that BLOCKS mutations until a human reconciles it, so 503 is
        // honest. An oversized file is a finding that blocks nothing — the
        // rest of the vault syncs normally — so it rides inside a 200 and the
        // monitor reads it out of the body. That difference is about
        // impairment, not about how long the condition lasts: both can persist
        // for days, and the monitor's cadence rules (one mail per failure-set
        // change, a reminder at most daily, one on recovery) are what keep
        // either from becoming noise.
        //
        // A WALK THAT COULD NOT COMPLETE is not a finding at all — it is a
        // broken instrument, reporting nothing about the vault. Both probes
        // treat it identically: degrade. Note this is NOT justified by "it
        // clears itself" — an unreadable directory or an expired budget can
        // persist exactly as long as a conflict file does. It is justified by
        // there being no measurement: "could not tell" rendered as "checked,
        // all clear" is the precise failure this backstop exists to prevent,
        // so it must not be how the backstop itself fails.
        var healthy = vaultReachable && rgVersion is not null && auditWritable
            && mutationsAllowed && conflictFiles is { Count: 0 } && oversized is not null;

        return new Report(
            healthy ? "ok" : "degraded",
            version,
            new VaultInfo(
                vaultReachable, resolver.Root, generation.Current,
                conflictFiles ?? [], conflictFiles is not null, _conflictScanError),
            new SyncInfo(syncOptions.Value.Mode, mutationsAllowed, heartbeatAge, blockedReason),
            new RipgrepInfo(rgVersion is not null, rgVersion),
            new AuditInfo(auditWritable, vaultOptions.Value.AuditLogPath),
            new OversizedInfo(
                oversized is not null, _oversizedScanError, oversized?.Count ?? 0,
                syncOptions.Value.MaxFileBytes, oversized ?? []));
    }

    /// <summary>
    /// Null when the walk could not complete — the same "could not tell" the
    /// oversized scan reports, and NOT an empty list. Letting the exception
    /// escape (which it did) breaks /health's own 200/503 contract: an
    /// unreadable directory anywhere in the vault answered 500, which the
    /// monitor can only report as "knapper degraded/down, tunnel down, or
    /// Access rejecting the token" — every diagnosis except the true one.
    /// </summary>
    private IReadOnlyList<string>? ScanConflicts()
    {
        try
        {
            _conflictScanError = null;
            return conflicts.ScanAll(ConflictScanBudget);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException)
        {
            // Labelled like the oversized walk's, because the two causes need
            // OPPOSITE responses: `io` is usually transient, while `timeout`
            // means the walk cannot finish in the budget and will keep not
            // finishing. TimeoutException MUST be caught here — the budget
            // exists to keep this walk off the request thread indefinitely,
            // and letting its expiry escape would turn a bounded walk into a
            // 500, which /health's own contract says it must never answer.
            _conflictScanError = e is TimeoutException ? $"timeout: {e.Message}" : $"io: {e.Message}";
            if (e is TimeoutException)
            {
                logger.LogWarning(e,
                    "Conflict-file walk exceeded its {Budget}s budget — /health reports it as unknown, " +
                    "not clean. This does not clear on its own: the vault has outgrown the budget.",
                    ConflictScanBudget.TotalSeconds);
            }
            else
            {
                logger.LogWarning(e,
                    "Conflict-file walk could not complete — /health reports it as unknown, not clean. " +
                    "The vault contains a directory this process cannot read.");
            }
            return null;
        }
    }

    private string? _conflictScanError;
    private string? _oversizedScanError;

    /// <summary>
    /// Every boolean means "probed, and fine" — an incomplete walk is false,
    /// never true, on BOTH walks. A failed conflict scan gets no field of its
    /// own here: for conflicts, found and could-not-tell both degrade, so the
    /// lone `ok` plus the status code already say everything the monitor acts
    /// on. Oversized is the only key where a false means two things needing
    /// different responses, and there the STATUS CODE separates them —
    /// files FOUND ride inside a 200 (the monitor reads .oversized.ok there,
    /// under check 1b, which is guarded on HTTP 200 precisely so it cannot
    /// misreport a failed scan as stranded files), while a walk that could not
    /// complete degrades to 503 and lands as check 1. Which files, and which
    /// half of a false is which, are /health's job — /up discloses no more
    /// than the monitor must act on.
    /// </summary>
    public UpReport CheckUp()
    {
        var report = Check();
        return new UpReport(
            report.Status,
            report.Version,
            new UpBool(report.Vault.Reachable),
            new UpBool(report.Sync.MutationsAllowed),
            new UpBool(report.Ripgrep.Available),
            new UpBool(report.Audit.Writable),
            new UpBool(report.Vault.ConflictScanComplete && report.Vault.ConflictFiles.Count == 0),
            new UpBool(report.Oversized is { Scanned: true, Count: 0 }));
    }

    private IReadOnlyList<string> _oversized = [];
    private long _oversizedScannedAt; // Stopwatch timestamp; 0 = never scanned

    /// <summary>
    /// Vault files Obsidian Sync refuses to carry. This is the BACKSTOP, not
    /// the guard: the mutation service refuses Knapper's own oversized writes,
    /// but one can still get here another way — a human shell on the CT, or a
    /// file predating the guard — and nothing else would notice, since Sync
    /// logs the rejection and prints "Fully synced" in the same millisecond.
    /// It canNOT see an oversized file made on a Mac: that one never arrives.
    ///
    /// Dot-directories are skipped for the same reason queries skip them: .git
    /// packfiles and .obsidian plugin bundles routinely exceed the ceiling,
    /// none of them sync, and reporting them would be permanent noise.
    ///
    /// NULL means the walk could not complete. It used to return the cached
    /// list here instead — correct-looking, and correct once a scan had
    /// succeeded, but the cache starts EMPTY, so a failure on the very first
    /// call reported the vault as clean. That first call is immediately after
    /// a service start: exactly when the monitor polls and exactly when
    /// transient IO problems are likeliest. The stale-list fallback is gone
    /// too — a list from a previous minute presented as this minute's answer
    /// is a smaller version of the same lie.
    /// </summary>
    private IReadOnlyList<string>? Oversized()
    {
        if (_oversizedScannedAt != 0 && Stopwatch.GetElapsedTime(_oversizedScannedAt) < OversizedTtl)
            return _oversized;

        try
        {
            _oversized = OversizedFiles.Scan(resolver.Root, syncOptions.Value.MaxFileBytes, OversizedBudget);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException)
        {
            // A partial walk must not be reported as "none found" — that is
            // the same lie as an empty search claiming exhaustive coverage.
            // Say "could not tell" and re-scan on the next request.
            //
            // The two causes are labelled because they need OPPOSITE
            // responses and are otherwise identical on every surface: `io`
            // is usually transient and often fixes itself, while `timeout`
            // means the walk cannot finish in the budget and will keep not
            // finishing — the vault has outgrown a design assumption, and the
            // lever is OversizedFiles.DefaultBudget, not patience.
            _oversizedScanError = e is TimeoutException ? $"timeout: {e.Message}" : $"io: {e.Message}";
            if (e is TimeoutException)
            {
                logger.LogWarning(e,
                    "Oversized-file walk exceeded its {Budget}s budget — the backstop is reporting nothing. " +
                    "This does not clear on its own: the vault has outgrown the budget.",
                    OversizedBudget.TotalSeconds);
            }
            else
            {
                logger.LogWarning(e,
                    "Oversized-file walk could not complete — /health reports it as unknown, not clean. " +
                    "The vault contains a directory this process cannot read.");
            }
            _oversized = [];
            _oversizedScannedAt = 0;
            return null;
        }

        _oversizedScanError = null;
        _oversizedScannedAt = Stopwatch.GetTimestamp();
        return _oversized;
    }

    private string? RipgrepVersion()
    {
        // A cached SUCCESS is honored only within the TTL; a failure is
        // never cached — "unknown" must re-probe, not report the last good
        // answer indefinitely.
        if (_ripgrepVersion is not null
            && _ripgrepCheckedAt != 0
            && Stopwatch.GetElapsedTime(_ripgrepCheckedAt) < RipgrepTtl)
        {
            return _ripgrepVersion;
        }
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = vaultOptions.Value.RipgrepPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--version");
            using var process = Process.Start(psi)!;
            // Wait BEFORE reading: --version output fits the pipe buffer, and
            // reading first would block forever on a hung process.
            if (!process.WaitForExit(RipgrepTimeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) { } // exited in the race window
                process.WaitForExit(2_000);
                return CacheRipgrep(null);
            }
            return CacheRipgrep(process.ExitCode == 0 ? process.StandardOutput.ReadLine() : null);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return CacheRipgrep(null);
        }
    }

    private string? CacheRipgrep(string? version)
    {
        _ripgrepCheckedAt = Stopwatch.GetTimestamp();
        return _ripgrepVersion = version;
    }

    private bool AuditWritable()
    {
        // A REPRESENTATIVE probe: write + fsync + delete a sibling probe file
        // in the audit directory. Merely opening the existing audit file can
        // succeed on a filesystem that no longer accepts a durable write
        // (full, read-only remount) — and a mutation whose audit append then
        // fails is exactly the failure this signal exists to surface. The
        // probe never touches the audit trail itself. The name is unique per
        // request: /health and /up can run concurrently, and a shared name
        // opened FileShare.None would let one probe fail the other with a
        // sharing violation — a spurious 503.
        var probePath = $"{vaultOptions.Value.AuditLogPath}.health-probe-{Guid.NewGuid():N}";
        try
        {
            // Sweep leftovers first: unique names mean a persistently
            // failing delete would otherwise accumulate one file per check.
            var directory = Path.GetDirectoryName(Path.GetFullPath(vaultOptions.Value.AuditLogPath))!;
            foreach (var stale in Directory.EnumerateFiles(directory, "*.health-probe-*"))
            {
                try
                {
                    File.Delete(stale);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            using (var stream = new FileStream(probePath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            }))
            {
                stream.Write("knapper-health-probe\n"u8);
                stream.Flush(flushToDisk: true);
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(probePath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
