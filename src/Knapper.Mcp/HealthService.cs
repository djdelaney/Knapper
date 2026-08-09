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
/// human needs to know).
/// </summary>
public sealed class HealthService(
    VaultPathResolver resolver,
    VaultGenerationCounter generation,
    ConflictDetector conflicts,
    ISyncGate syncGate,
    IOptions<VaultOptions> vaultOptions,
    IOptions<SyncOptions> syncOptions)
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

    public sealed record Report(
        string Status,
        string Version,
        VaultInfo Vault,
        SyncInfo Sync,
        RipgrepInfo Ripgrep,
        AuditInfo Audit);

    public sealed record VaultInfo(bool Reachable, string Root, long Generation, IReadOnlyList<string> ConflictFiles);

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
        UpBool Conflicts);

    public sealed record UpBool(bool Ok);

    public Report Check()
    {
        var version = typeof(HealthService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        var vaultReachable = Directory.Exists(resolver.Root);
        var conflictFiles = vaultReachable ? conflicts.ScanAll() : [];

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

        var healthy = vaultReachable && rgVersion is not null && auditWritable
            && mutationsAllowed && conflictFiles.Count == 0;

        return new Report(
            healthy ? "ok" : "degraded",
            version,
            new VaultInfo(vaultReachable, resolver.Root, generation.Current, conflictFiles),
            new SyncInfo(syncOptions.Value.Mode, mutationsAllowed, heartbeatAge, blockedReason),
            new RipgrepInfo(rgVersion is not null, rgVersion),
            new AuditInfo(auditWritable, vaultOptions.Value.AuditLogPath));
    }

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
            new UpBool(report.Vault.ConflictFiles.Count == 0));
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
