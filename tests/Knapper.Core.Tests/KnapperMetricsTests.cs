using System.Text.Json;
using Knapper.Core;
using Knapper.Core.Mutation;

namespace Knapper.Core.Tests;

/// <summary>
/// The metrics snapshot is the external monitor's only window into query
/// outcome rates and — critically — audit-append failures (brief §8). It
/// must be durable, bounded, atomic to read, and must never fail the
/// operation that produced the event.
/// </summary>
public sealed class KnapperMetricsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string MetricsPath => Path.Combine(_dir.Path, "metrics.json");

    [Fact]
    public void Audit_append_failure_is_flushed_immediately_and_durably()
    {
        using var metrics = new KnapperMetrics(MetricsPath) { FlushInterval = TimeSpan.FromHours(1) };
        metrics.RecordToolOutcome("ok"); // throttled — may or may not be on disk yet

        metrics.RecordAuditAppendFailure(); // must NOT wait for the throttle

        var json = JsonDocument.Parse(File.ReadAllText(MetricsPath)).RootElement;
        json.GetProperty("AuditAppendFailures").GetInt64().ShouldBe(1);
        json.GetProperty("StartedAt").GetDateTimeOffset(); // restart-detection stamp present
    }

    [Fact]
    public void Snapshot_file_is_one_bounded_json_object_with_all_counters()
    {
        using var metrics = new KnapperMetrics(MetricsPath) { FlushInterval = TimeSpan.Zero };
        metrics.RecordToolOutcome("ok");
        metrics.RecordToolOutcome(nameof(VaultErrorCode.QueryTimeout));
        metrics.RecordCompleteness(truncated: true, generationChanged: true);
        metrics.TryFlush();

        var json = JsonDocument.Parse(File.ReadAllText(MetricsPath)).RootElement;
        json.GetProperty("ToolCalls").GetInt64().ShouldBe(2);
        json.GetProperty("ToolErrors").GetInt64().ShouldBe(1);
        json.GetProperty("QueryTimeouts").GetInt64().ShouldBe(1);
        json.GetProperty("TruncatedResponses").GetInt64().ShouldBe(1);
        json.GetProperty("GenerationChangedResponses").GetInt64().ShouldBe(1);
        Directory.EnumerateFiles(_dir.Path).ShouldHaveSingleItem(); // no temp residue
    }

    [Fact]
    public void A_broken_metrics_disk_never_fails_the_recording_operation()
    {
        using var metrics = new KnapperMetrics(MetricsPath) { FlushInterval = TimeSpan.Zero };
        metrics.TryFlush();
        File.SetUnixFileMode(_dir.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Should.NotThrow(() => metrics.RecordAuditAppendFailure());
            metrics.Read().AuditAppendFailures.ShouldBe(1); // still counted in memory
        }
        finally
        {
            File.SetUnixFileMode(_dir.Path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Audit_log_append_failures_reach_the_metrics_sink()
    {
        using var metrics = new KnapperMetrics(MetricsPath);
        var auditPath = Path.Combine(_dir.Path, "audit.jsonl");
        var audit = new AuditLog(auditPath, metrics);
        audit.Append(new AuditLog.Entry(DateTimeOffset.UtcNow, "edit", "a.md", "ok"));

        File.SetUnixFileMode(auditPath, UnixFileMode.UserRead); // append now fails
        try
        {
            Should.Throw<UnauthorizedAccessException>(() =>
                audit.Append(new AuditLog.Entry(DateTimeOffset.UtcNow, "edit", "b.md", "ok")));
            metrics.Read().AuditAppendFailures.ShouldBe(1);
        }
        finally
        {
            File.SetUnixFileMode(auditPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
