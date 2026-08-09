using Knapper.Core.Interop;

namespace Knapper.Core.Vault;

/// <summary>
/// The only code that writes vault bytes. Ports the commit discipline of
/// <c>vault-edit.reference.py</c>:
/// hidden same-directory temp → fsync → last-instant SHA re-check →
/// atomic rename (replace) or hard-link (no-clobber create) → directory
/// fsync — with temps cleaned on every failure path, so Obsidian Sync never
/// sees a half-written note and never syncs a stray temp.
///
/// <para>Callers hold the per-path lock across read → check → transform →
/// these calls → <see cref="VerifyOnDisk"/>. The SHA re-check inside
/// <see cref="Replace"/> is a second line, not the lock's replacement: it
/// catches writers that bypass the lock protocol (a human shell, Sync itself
/// delivering a remote edit mid-flight).</para>
/// </summary>
public static class AtomicFile
{
    /// <summary>Hidden + Sync-ignored + gitignored + unaddressable through the resolver.</summary>
    public const string TempPrefix = ".knapper-tmp-";

    /// <summary>
    /// Atomically replace an existing file's bytes, preserving its mode.
    /// Throws <see cref="VaultErrorCode.PreconditionFailed"/> — mutating
    /// nothing — if the bytes on disk no longer hash to
    /// <paramref name="expectedSha256"/>.
    /// </summary>
    public static void Replace(string absolutePath, byte[] data, string expectedSha256)
    {
        var directory = Path.GetDirectoryName(absolutePath)!;
        UnixFileMode mode;
        try
        {
            mode = File.GetUnixFileMode(absolutePath);
        }
        catch (FileNotFoundException)
        {
            throw new KnapperException(VaultErrorCode.NotFound, $"no such file: {absolutePath}");
        }

        var temp = WriteTemp(directory, MaybeInjectShortWrite(absolutePath, data), mode);
        var committed = false;
        try
        {
            var latest = File.ReadAllBytes(absolutePath);
            if (!VaultHash.Matches(expectedSha256, latest))
            {
                throw new KnapperException(VaultErrorCode.PreconditionFailed,
                    "precondition failed while the write was being prepared — the file changed; " +
                    $"re-read and rebuild against current content (expected {expectedSha256}, found {VaultHash.Sha256Hex(latest)})");
            }
            File.Move(temp, absolutePath, overwrite: true); // rename(2): atomic
            committed = true;
        }
        finally
        {
            if (!committed)
                TryDelete(temp);
        }
        Posix.FsyncDirectory(directory);
    }

    /// <summary>
    /// Atomic no-clobber create via the hard-link pattern — cannot replace a
    /// file that appears concurrently, unlike exists-then-rename. The parent
    /// directory must already exist: folder creation is a deliberate act.
    /// </summary>
    public static void CreateNew(string absolutePath, byte[] data)
    {
        var directory = Path.GetDirectoryName(absolutePath)!;
        if (!Directory.Exists(directory))
        {
            throw new KnapperException(VaultErrorCode.NotFound,
                $"parent directory does not exist: {directory} (folder creation is a deliberate act — do it explicitly first)");
        }

        var temp = WriteTemp(directory, data,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        try
        {
            Posix.Link(temp, absolutePath);
        }
        finally
        {
            TryDelete(temp);
        }
        Posix.FsyncDirectory(directory);
    }

    /// <summary>
    /// Reopen and byte-compare. Every mutation ends here — this vault has a
    /// documented history of writes that reported success without landing,
    /// so a success receipt is never trusted without rereading the bytes.
    /// </summary>
    public static void VerifyOnDisk(string absolutePath, byte[] expectedBytes)
    {
        var onDisk = File.ReadAllBytes(absolutePath);
        if (!onDisk.AsSpan().SequenceEqual(expectedBytes))
        {
            throw new KnapperException(VaultErrorCode.VerifyFailed,
                "post-write verification failed: bytes on disk differ from what was written " +
                $"(concurrent write landed?) — sha on disk {VaultHash.Sha256Hex(onDisk)}");
        }
    }

    /// <summary>
    /// Deterministic short-write FAULT INJECTOR for the acceptance suite
    /// (brief §13: an induced short write must be caught by the post-write
    /// reopen/byte-compare). Inert unless the KNAPPER_FAULT_SHORT_WRITE env
    /// var names a substring of the target path; then the temp receives only
    /// half the bytes while every success signal proceeds normally —
    /// <see cref="VerifyOnDisk"/> is what must notice. This is an injector,
    /// not a bypass: it can only BREAK a write that would otherwise verify;
    /// there is no way to use it to land unverified content as success.
    /// </summary>
    private static byte[] MaybeInjectShortWrite(string absolutePath, byte[] data)
    {
        var trigger = Environment.GetEnvironmentVariable("KNAPPER_FAULT_SHORT_WRITE");
        if (string.IsNullOrEmpty(trigger) || !absolutePath.Contains(trigger, StringComparison.Ordinal))
            return data;
        return data[..(data.Length / 2)];
    }

    /// <summary>Complete, fsynced hidden temp in the target's own directory; deleted on any failure.</summary>
    private static string WriteTemp(string directory, byte[] data, UnixFileMode mode)
    {
        var temp = Path.Combine(directory, TempPrefix + Guid.NewGuid().ToString("N"));
        try
        {
            using var stream = new FileStream(temp, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            // fchmod after create, not UnixCreateMode alone: the create mode is
            // filtered by umask, and Replace must preserve the target's mode exactly.
            File.SetUnixFileMode(stream.SafeFileHandle, mode);
            stream.Write(data);
            stream.Flush(flushToDisk: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        return temp;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
