using Knapper.Core.Generation;
using Knapper.Core.Mutation;
using Knapper.Core.Options;
using Knapper.Core.Vault;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Knapper.Mcp.Tests;

/// <summary>
/// Health must DEGRADE when a dependency breaks after startup: a
/// permanently cached probe (or a non-representative one) leaves /up
/// healthy while agents fail — the monitor never fires and the outage is
/// silent. These tests break each dependency and watch the report flip.
/// </summary>
public sealed class HealthServiceTests : IDisposable
{
    private readonly string _vaultDir = Directory.CreateTempSubdirectory("knapper-health-vault-").FullName;
    private readonly string _outsideDir = Directory.CreateTempSubdirectory("knapper-health-outside-").FullName;
    private readonly VaultGenerationCounter _generation = new();

    public void Dispose()
    {
        _generation.Dispose();
        RestoreWritable(Path.Combine(_outsideDir, "audit"));
        TryDelete(_vaultDir);
        TryDelete(_outsideDir);
    }

    private HealthService NewService(string ripgrepPath, string auditDir)
    {
        var resolver = new VaultPathResolver(_vaultDir);
        Directory.CreateDirectory(auditDir);
        var vaultOptions = new VaultOptions
        {
            RootPath = _vaultDir,
            AuditLogPath = Path.Combine(auditDir, "audit.jsonl"),
            RipgrepPath = ripgrepPath,
        };
        return new HealthService(
            resolver, _generation, new ConflictDetector(resolver), StaticSyncGate.Open,
            Options.Create(vaultOptions), Options.Create(new SyncOptions { Mode = "open" }),
            NullLogger<HealthService>.Instance)
        {
            RipgrepTtl = TimeSpan.Zero, // every Check re-probes
        };
    }

    private string WriteFakeRipgrep(string script)
    {
        var path = Path.Combine(_outsideDir, "fake-rg");
        File.WriteAllText(path, "#!/bin/sh\n" + script + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public void Ripgrep_breaking_after_a_successful_probe_degrades_health()
    {
        var rg = WriteFakeRipgrep("echo 'ripgrep 999.0.0 (health probe)'");
        var health = NewService(rg, Path.Combine(_outsideDir, "audit"));

        health.Check().Ripgrep.Available.ShouldBeTrue();

        File.Delete(rg); // rg removed AFTER the first success
        var report = health.Check();
        report.Ripgrep.Available.ShouldBeFalse();
        report.Status.ShouldBe("degraded");
    }

    [Fact]
    public void A_hung_ripgrep_is_killed_and_reported_unavailable()
    {
        var rg = WriteFakeRipgrep("sleep 30");
        var health = NewService(rg, Path.Combine(_outsideDir, "audit"));
        health.RipgrepTimeoutMs = 300;

        var report = health.Check();
        report.Ripgrep.Available.ShouldBeFalse();
        report.Status.ShouldBe("degraded");
    }

    [Fact]
    public void Audit_probe_detects_a_directory_that_stops_accepting_writes()
    {
        var rg = WriteFakeRipgrep("echo 'ripgrep 999.0.0'");
        var auditDir = Path.Combine(_outsideDir, "audit");
        var health = NewService(rg, auditDir);

        health.Check().Audit.Writable.ShouldBeTrue();

        // Read-only audit dir: the old open-only probe still succeeded here
        // when the audit FILE already existed — the write+fsync probe fails.
        File.SetUnixFileMode(auditDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var report = health.Check();
            report.Audit.Writable.ShouldBeFalse();
            report.Status.ShouldBe("degraded");
        }
        finally
        {
            RestoreWritable(auditDir);
        }
    }

    [Fact]
    public void Audit_probe_leaves_no_probe_file_behind()
    {
        var rg = WriteFakeRipgrep("echo 'ripgrep 999.0.0'");
        var auditDir = Path.Combine(_outsideDir, "audit");
        var health = NewService(rg, auditDir);

        health.Check().Audit.Writable.ShouldBeTrue();
        Directory.EnumerateFiles(auditDir).ShouldBeEmpty(); // probe cleaned, audit untouched
    }

    [Fact]
    public void Concurrent_health_checks_never_fail_each_others_audit_probe()
    {
        // /health and /up can run at the same time; a shared probe filename
        // opened FileShare.None let one request 503 the other. Hammer it.
        var rg = WriteFakeRipgrep("echo 'ripgrep 999.0.0'");
        var auditDir = Path.Combine(_outsideDir, "audit");
        var health = NewService(rg, auditDir);

        var reports = new HealthService.Report[64];
        Parallel.For(0, reports.Length, i => reports[i] = health.Check());

        reports.ShouldAllBe(r => r.Audit.Writable, "no probe may see another probe as a sharing violation");
        Directory.EnumerateFiles(auditDir).ShouldBeEmpty();
    }

    /// <summary>
    /// A false clean is invisible by construction: nobody investigates a
    /// report that says "checked, all clear". The cache starts EMPTY, so a
    /// walk that throws on the very first call used to return that empty
    /// list — "could not tell" rendered as "scanned, none found", with
    /// /up's oversized.ok true. Worst case is well aligned with reality:
    /// the cold cache exists immediately after a start, which is exactly
    /// when a monitor polls and when transient IO problems are likeliest.
    ///
    /// Non-root, like the read-only audit-dir test above: mode 000 is what
    /// makes the walk throw.
    /// </summary>
    [Fact]
    public void A_scan_that_fails_on_the_FIRST_call_reports_unknown_not_clean()
    {
        var rg = WriteFakeRipgrep("echo 'ripgrep 999.0.0'");
        var health = NewService(rg, Path.Combine(_outsideDir, "audit"));

        var unreadable = Path.Combine(_vaultDir, "Locked");
        Directory.CreateDirectory(unreadable);
        File.SetUnixFileMode(unreadable, UnixFileMode.None);
        try
        {
            var report = health.Check();

            report.Oversized.Scanned.ShouldBeFalse();
            report.Oversized.Count.ShouldBe(0); // 0 counted, NOT 0 present
            report.Status.ShouldBe("degraded");

            // The same directory defeats the conflict walk, which used to
            // throw straight out of Check() — /health answered 500 and broke
            // its own 200/503 contract before the oversized scan even ran.
            report.Vault.ConflictScanComplete.ShouldBeFalse();

            // Both causes are labelled. An IO failure is usually transient;
            // a budget expiry never is. Identical on every other surface,
            // opposite responses, so the payload must tell them apart.
            report.Oversized.ScanError.ShouldStartWith("io:");
            report.Vault.ConflictScanError.ShouldStartWith("io:");
        }
        finally
        {
            RestoreWritable(unreadable);
        }
    }

    /// <summary>
    /// The oversized walk failing ALONE — the permission test above defeats
    /// both walks at once, so on its own it cannot show which probe reported
    /// the unknown. An expired budget reaches only this one.
    /// </summary>
    [Fact]
    public void A_walk_that_runs_out_of_budget_degrades_health_rather_than_hanging_it()
    {
        var rg = WriteFakeRipgrep("echo 'ripgrep 999.0.0'");
        var health = NewService(rg, Path.Combine(_outsideDir, "audit"));
        health.OversizedBudget = TimeSpan.Zero;
        File.WriteAllText(Path.Combine(_vaultDir, "note.md"), "small\n");

        var report = health.Check();

        report.Oversized.Scanned.ShouldBeFalse();
        report.Status.ShouldBe("degraded");
        report.Oversized.ScanError.ShouldStartWith("timeout:");
        report.Vault.ConflictScanComplete.ShouldBeTrue(); // the other walk is unaffected
        report.Vault.ConflictScanError.ShouldBeNull();
    }

    /// <summary>
    /// The unknown state must not stick: a failure is never cached, or the
    /// probe would keep answering "could not tell" for a TTL after the vault
    /// became readable again — a self-clearing fault that stops self-clearing.
    /// </summary>
    [Fact]
    public void A_failed_scan_is_not_cached()
    {
        var rg = WriteFakeRipgrep("echo 'ripgrep 999.0.0'");
        var health = NewService(rg, Path.Combine(_outsideDir, "audit"));
        health.OversizedBudget = TimeSpan.Zero;

        health.Check().Oversized.Scanned.ShouldBeFalse();

        health.OversizedBudget = OversizedFiles.DefaultBudget;
        var report = health.Check();
        report.Oversized.Scanned.ShouldBeTrue();
        report.Oversized.ScanError.ShouldBeNull(); // the reason clears with the state
        report.Status.ShouldBe("ok");
    }

    private static void RestoreWritable(string dir)
    {
        if (!Directory.Exists(dir))
            return;
        try
        {
            File.SetUnixFileMode(dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (IOException) { }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
