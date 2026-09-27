using System.Text.Json;
using Knapper.Core.Git;
using Knapper.Core.Interop;
using Knapper.Core.Vault;

namespace Knapper.Core.Query;

/// <summary>The accepted baseline: the commit whose findings are the standing backlog.</summary>
public sealed record LintBaseline(string Commit, DateTimeOffset AcceptedAt);

/// <summary>
/// The lint baseline's one record on disk (<c>Lint:BaselinePath</c>), outside
/// the vault. Read on every lint call rather than cached, so an accept takes
/// effect on the running server without a restart.
///
/// <para>A record that exists but cannot be used is an ERROR, never "no
/// baseline". Treating it as absent would quietly swap "what changed" for
/// the whole backlog — loud, but wrong about why — and treating it as
/// "nothing new" would be the silent failure this whole feature exists to
/// prevent. Absent is the only state that means "no baseline".</para>
/// </summary>
public sealed class LintBaselineStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;

    /// <summary>An unset path: every lint reports everything, and accept refuses.</summary>
    public static LintBaselineStore None { get; } = new("");

    public LintBaselineStore(string path) => _path = path ?? "";

    public bool Configured => !string.IsNullOrWhiteSpace(_path);

    public string Path => _path;

    /// <summary>Null when the operator declared no baseline. Refuses a path inside the vault, as startup does.</summary>
    public static string? ValidatePath(string path, string vaultRoot) =>
        string.IsNullOrWhiteSpace(path)
            ? null
            : !System.IO.Path.IsPathRooted(path)
                ? $"Lint:BaselinePath ('{path}') must be absolute"
                : PathContainment.IsInsideOrEqual(path, vaultRoot)
                    ? $"Lint:BaselinePath ('{path}') is the vault or INSIDE it — the baseline decides which findings " +
                      "are silenced, so nothing lint reports on may be able to write it"
                    : null;

    /// <summary>The accepted baseline, or null when none is configured or none has been accepted yet.</summary>
    public LintBaseline? Read()
    {
        if (!Configured)
            return null;
        byte[] bytes;
        try
        {
            // No-follow, regular files only. Containment is proved at boot,
            // but a DANGLING symlink canonicalizes lexically and passes it —
            // and one aimed at a vault path an agent later creates would hand
            // the record that decides what is silenced to the agents lint
            // reports on. A symlink here is refused, never followed.
            using var handle = Posix.OpenRegularForRead(_path, _path);
            using var stream = new FileStream(handle, FileAccess.Read);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }
        catch (KnapperException e) when (e.Code == VaultErrorCode.NotFound)
        {
            return null;
        }
        catch (KnapperException e)
        {
            throw Unusable($"it could not be read: {e.Message}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw Unusable($"it could not be read: {e.Message}");
        }

        Record? record;
        try
        {
            record = JsonSerializer.Deserialize<Record>(bytes, Json);
        }
        catch (JsonException e)
        {
            throw Unusable($"it is not valid JSON ({e.Message})");
        }
        if (record is null || record.Commit is null || !GitTreeReader.IsCommitName(record.Commit))
            throw Unusable("it does not name a full commit");
        if (record.AcceptedAt is not { } acceptedAt)
            throw Unusable("it carries no acceptedAt");
        return new LintBaseline(record.Commit, acceptedAt);
    }

    /// <summary>Durably record <paramref name="baseline"/>: temp + fsync + rename + directory fsync.</summary>
    public void Write(LintBaseline baseline)
    {
        if (!Configured)
        {
            throw new KnapperException(VaultErrorCode.InvalidArgument,
                "Lint:BaselinePath is not configured — there is nowhere to record an accepted baseline");
        }
        var full = System.IO.Path.GetFullPath(_path);
        var directory = System.IO.Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        var temp = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead
                    | UnixFileMode.OtherRead,
            }))
            {
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(
                    new Record { Commit = baseline.Commit, AcceptedAt = baseline.AcceptedAt }, Json));
                stream.Flush(flushToDisk: true);
            }
            // Atomic: a reader sees the old baseline or the new one, never a
            // half-written file (which Read would refuse, failing every lint).
            File.Move(temp, full, overwrite: true);
            Posix.FsyncDirectory(directory);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private KnapperException Unusable(string why) => new(VaultErrorCode.IoError,
        $"the lint baseline at {_path} is unusable: {why}. Re-accept one with `knapper lint --accept`, or pass " +
        "all=true to see every finding without it");

    private sealed class Record
    {
        public string? Commit { get; set; }
        public DateTimeOffset? AcceptedAt { get; set; }
    }
}
