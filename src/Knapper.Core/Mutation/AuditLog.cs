using System.Text;
using System.Text.Json;

namespace Knapper.Core.Mutation;

/// <summary>
/// Append-only JSONL audit log, one line per mutation attempt — including
/// REJECTED ones (a stale-write rejection is signal, not noise; brief §8).
/// Lives OUTSIDE the vault: it must never sync, and vault content must
/// never be able to touch it. Writes are fsynced — an audit line that
/// vanishes in a crash after the mutation landed would break the "audit
/// explains every change" property the git history relies on.
/// </summary>
public sealed class AuditLog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly Lock _writeLock = new();

    public AuditLog(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new KnapperException(VaultErrorCode.IoError, "audit log path is not configured");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _path = path;
    }

    public sealed record Entry(
        DateTimeOffset At,
        string Op,
        string Path,
        string Outcome,
        string? Client = null,
        string? RequestId = null,
        string? BeforeSha256 = null,
        string? AfterSha256 = null,
        string? Detail = null);

    public void Append(Entry entry)
    {
        var line = JsonSerializer.Serialize(entry, JsonOptions) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        lock (_writeLock)
        {
            using var stream = new FileStream(_path, new FileStreamOptions
            {
                Mode = FileMode.Append,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
    }
}
