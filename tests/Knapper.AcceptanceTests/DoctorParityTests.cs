using System.Diagnostics;
using System.Reflection;

namespace Knapper.AcceptanceTests;

/// <summary>
/// <c>knapper doctor</c> must not call a configuration healthy that the
/// server refuses to start on, or that blocks every mutation once it is
/// running. The deploy script runs doctor under the live service's
/// environment and trusts the answer, so each disagreement was a green
/// deployment gate over a dead or refused service (security review
/// 2026-11-24, F2). These run the REAL CLI binary: doctor is top-level
/// program code, and a unit test of the shared helpers would pass while
/// doctor went on not calling them.
/// </summary>
public sealed class DoctorParityTests : IDisposable
{
    private readonly string _vaultDir = Directory.CreateTempSubdirectory("knapper-doctor-vault-").FullName;
    private readonly string _outsideDir = Directory.CreateTempSubdirectory("knapper-doctor-out-").FullName;
    private string Heartbeat => Path.Combine(_outsideDir, "heartbeat");

    public void Dispose()
    {
        Directory.Delete(_vaultDir, recursive: true);
        Directory.Delete(_outsideDir, recursive: true);
    }

    private void TouchHeartbeat(TimeSpan offset)
    {
        File.WriteAllText(Heartbeat, "");
        File.SetLastWriteTimeUtc(Heartbeat, DateTime.UtcNow + offset);
    }

    [Fact]
    public void A_heartbeat_stamped_in_the_future_fails_doctor_as_it_blocks_mutations()
    {
        TouchHeartbeat(TimeSpan.FromHours(2));
        var lines = Doctor(new() { ["Sync__Mode"] = "heartbeat", ["Sync__HeartbeatPath"] = Heartbeat });

        var line = lines.Single(l => l.Contains("sync heartbeat fresh", StringComparison.Ordinal));
        line.ShouldStartWith("FAIL ");
        line.ShouldContain("FUTURE");
    }

    [Fact]
    public void A_fresh_heartbeat_passes_and_the_mode_is_read_case_insensitively_like_the_server()
    {
        TouchHeartbeat(TimeSpan.Zero);
        var lines = Doctor(new() { ["Sync__Mode"] = "Heartbeat", ["Sync__HeartbeatPath"] = Heartbeat });

        lines.Single(l => l.Contains("sync heartbeat fresh", StringComparison.Ordinal))
            .ShouldStartWith("ok   ");
        lines.ShouldNotContain(l => l.Contains("sync gate is OPEN", StringComparison.Ordinal),
            "the server gates on 'Heartbeat'; doctor must not report it as open");
    }

    [Fact]
    public void A_sync_mode_the_server_refuses_to_start_on_fails_doctor()
    {
        var lines = Doctor(new() { ["Sync__Mode"] = "hearbeat" });

        lines.Single(l => l.Contains("Sync:Mode is ", StringComparison.Ordinal)).ShouldStartWith("FAIL ");
        lines.ShouldNotContain(l => l.Contains("sync gate is OPEN", StringComparison.Ordinal));
    }

    [Fact]
    public void An_open_sync_gate_still_only_warns()
    {
        var lines = Doctor(new() { ["Sync__Mode"] = "open" });

        lines.ShouldContain(l => l.StartsWith("warn  sync gate is OPEN", StringComparison.Ordinal));
        lines.Single(l => l.Contains("Sync:Mode is ", StringComparison.Ordinal)).ShouldStartWith("ok   ");
    }

    [Fact]
    public void Access_enabled_with_no_audience_fails_the_ingress_check()
    {
        var lines = Doctor(new()
        {
            ["Sync__Mode"] = "open",
            ["Mcp__Access__Enabled"] = "true",
            ["Mcp__Access__TeamDomain"] = "https://team.example.invalid",
        });

        var line = lines.Single(l => l.Contains("Ingress (", StringComparison.Ordinal));
        line.ShouldStartWith("FAIL ");
        line.ShouldContain("Audience is empty");
    }

    [Fact]
    public void A_monitoring_audience_equal_to_the_owner_audience_fails_the_ingress_check()
    {
        var lines = Doctor(new()
        {
            ["Sync__Mode"] = "open",
            ["Mcp__Access__Enabled"] = "true",
            ["Mcp__Access__TeamDomain"] = "https://team.example.invalid",
            ["Mcp__Access__Audience"] = "aud-one",
            ["Mcp__Access__MonitoringAudience"] = "aud-one",
        });

        lines.Single(l => l.Contains("Ingress (", StringComparison.Ordinal)).ShouldStartWith("FAIL ");
    }

    [Fact]
    public void A_public_allowed_host_with_Access_off_fails_the_ingress_check()
    {
        var lines = Doctor(new()
        {
            ["Sync__Mode"] = "open",
            ["Mcp__AllowedHosts__0"] = "mcp.example.invalid",
        });

        var line = lines.Single(l => l.Contains("Ingress (", StringComparison.Ordinal));
        line.ShouldStartWith("FAIL ");
        line.ShouldContain("mcp.example.invalid");
    }

    [Fact]
    public void The_plain_loopback_dev_posture_still_passes_the_ingress_check()
    {
        var lines = Doctor(new() { ["Sync__Mode"] = "open" });

        lines.Single(l => l.Contains("Ingress (", StringComparison.Ordinal)).ShouldStartWith("ok   ");
    }

    private string[] Doctor(Dictionary<string, string> env)
    {
        var dll = typeof(DoctorParityTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "KnapperCliDll").Value!;
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = _outsideDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("doctor");
        // A parent environment carrying Knapper settings must not leak in.
        foreach (var key in psi.Environment.Keys
                     .Where(k => k.StartsWith("Mcp__", StringComparison.Ordinal)
                         || k.StartsWith("Sync__", StringComparison.Ordinal)
                         || k.StartsWith("Vault__", StringComparison.Ordinal)).ToList())
        {
            psi.Environment.Remove(key);
        }
        psi.Environment["Vault__RootPath"] = _vaultDir;
        psi.Environment["Vault__LockDirectory"] = Path.Combine(_outsideDir, "locks");
        psi.Environment["Vault__AuditLogPath"] = Path.Combine(_outsideDir, "audit.jsonl");
        foreach (var (key, value) in env)
            psi.Environment[key] = value;
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit(60_000).ShouldBeTrue("knapper doctor did not finish");
        return (stdout.Result + stderr.Result).Split('\n');
    }
}
