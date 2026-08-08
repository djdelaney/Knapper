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
        if (_ripgrepVersion is not null)
            return _ripgrepVersion;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = vaultOptions.Value.RipgrepPath,
                RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add("--version");
            using var process = Process.Start(psi)!;
            var firstLine = process.StandardOutput.ReadLine();
            process.WaitForExit(5_000);
            return _ripgrepVersion = firstLine;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private bool AuditWritable()
    {
        try
        {
            // Open-append-close without writing: proves permissions without
            // polluting the audit trail.
            using var _ = new FileStream(vaultOptions.Value.AuditLogPath, new FileStreamOptions
            {
                Mode = FileMode.Append,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
