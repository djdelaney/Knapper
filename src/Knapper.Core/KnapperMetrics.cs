using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Knapper.Core;

/// <summary>
/// The bounded, durable event surface the external monitor reads (brief §8):
/// a fixed set of cumulative counters snapshotted as one line of JSON to a
/// file OUTSIDE the vault. The host monitor computes rates as deltas between
/// its own runs — like /proc counters — and <c>startedAt</c> lets it tell a
/// process restart (counters legitimately reset) from a stalled server.
/// Bounded by construction: the counter set is fixed, the file never grows.
/// Flushes are throttled except for audit-append failures, which are the one
/// signal that must survive an immediate crash — those flush synchronously.
/// </summary>
public sealed class KnapperMetrics : IDisposable
{
    public sealed record Snapshot(
        DateTimeOffset StartedAt,
        DateTimeOffset FlushedAt,
        long ToolCalls,
        long ToolErrors,
        long QueryTimeouts,
        long StaleRejections,
        long IoErrors,
        long TruncatedResponses,
        long GenerationChangedResponses,
        long AuditAppendFailures);

    private readonly string? _path;
    private readonly Lock _flushLock = new();
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private long _toolCalls;
    private long _toolErrors;
    private long _queryTimeouts;
    private long _staleRejections;
    private long _ioErrors;
    private long _truncatedResponses;
    private long _generationChangedResponses;
    private long _auditAppendFailures;
    private long _lastFlush; // Stopwatch timestamp; 0 = never

    /// <summary>Throttle for routine counter flushes. Internal so tests can collapse it.</summary>
    internal TimeSpan FlushInterval = TimeSpan.FromSeconds(10);

    /// <summary>Null/empty path = count in memory only (dev, tests, CLI).</summary>
    public KnapperMetrics(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        if (_path is not null)
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    /// <summary>Outcome is "ok", a VaultErrorCode name, "cancelled", or "internal" — the ToolSupport vocabulary.</summary>
    public void RecordToolOutcome(string outcome)
    {
        Interlocked.Increment(ref _toolCalls);
        switch (outcome)
        {
            case "ok":
            case "cancelled": // client-driven, not a server error
                break;
            case nameof(VaultErrorCode.QueryTimeout):
                Interlocked.Increment(ref _toolErrors);
                Interlocked.Increment(ref _queryTimeouts);
                break;
            case nameof(VaultErrorCode.PreconditionFailed):
                Interlocked.Increment(ref _toolErrors);
                Interlocked.Increment(ref _staleRejections);
                break;
            case nameof(VaultErrorCode.IoError):
                Interlocked.Increment(ref _toolErrors);
                Interlocked.Increment(ref _ioErrors);
                break;
            default:
                Interlocked.Increment(ref _toolErrors);
                break;
        }
        FlushIfDue();
    }

    /// <summary>Completeness signals from any successful query/read result.</summary>
    public void RecordCompleteness(bool truncated, bool generationChanged)
    {
        if (truncated)
            Interlocked.Increment(ref _truncatedResponses);
        if (generationChanged)
            Interlocked.Increment(ref _generationChangedResponses);
        FlushIfDue();
    }

    /// <summary>
    /// The critical counter: a mutation landed (or was rejected) and its
    /// audit append failed. Flushed synchronously — this is the durable
    /// trace that explains an unaudited change, so it must not sit in a
    /// throttle window when the process is likely unhealthy.
    /// </summary>
    public void RecordAuditAppendFailure()
    {
        Interlocked.Increment(ref _auditAppendFailures);
        TryFlush();
    }

    public Snapshot Read() => new(
        _startedAt, DateTimeOffset.UtcNow,
        Interlocked.Read(ref _toolCalls),
        Interlocked.Read(ref _toolErrors),
        Interlocked.Read(ref _queryTimeouts),
        Interlocked.Read(ref _staleRejections),
        Interlocked.Read(ref _ioErrors),
        Interlocked.Read(ref _truncatedResponses),
        Interlocked.Read(ref _generationChangedResponses),
        Interlocked.Read(ref _auditAppendFailures));

    public void Dispose() => TryFlush();

    private void FlushIfDue()
    {
        if (_path is null)
            return;
        var last = Interlocked.Read(ref _lastFlush);
        if (last != 0 && Stopwatch.GetElapsedTime(last) < FlushInterval)
            return;
        TryFlush();
    }

    /// <summary>
    /// Best-effort by design: metrics must never fail the operation that
    /// produced them (a metrics-disk failure surfaces as a stale
    /// <c>flushedAt</c>, which the monitor treats as its own alert).
    /// Atomic temp+rename so the monitor never reads a torn line.
    /// </summary>
    internal void TryFlush()
    {
        if (_path is null)
            return;
        lock (_flushLock)
        {
            Interlocked.Exchange(ref _lastFlush, Stopwatch.GetTimestamp());
            var temp = _path + ".tmp";
            try
            {
                var json = JsonSerializer.Serialize(Read());
                using (var stream = new FileStream(temp, new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                        | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                }))
                {
                    stream.Write(Encoding.UTF8.GetBytes(json));
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
