using Knapper.Core.Generation;
using Knapper.Core.Mutation;
using Knapper.Core.Options;
using Knapper.Core.Vault;
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
            Options.Create(vaultOptions), Options.Create(new SyncOptions { Mode = "open" }))
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
