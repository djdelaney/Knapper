using Knapper.Core.Interop;

namespace Knapper.Core.Vault;

/// <summary>
/// The only code that writes vault bytes. Ports the commit discipline of
/// <c>vault-edit.reference.py</c>:
/// hidden same-directory temp → fsync → last-instant SHA re-check →
/// atomic exchange (replace) or hard-link (no-clobber create) → directory
/// fsync — with temps cleaned on every failure path, so Obsidian Sync never
/// sees a half-written note and never syncs a stray temp.
///
/// <para>Callers hold the per-path lock across read → check → transform →
/// these calls → <see cref="VerifyOnDisk"/>. The SHA re-check inside
/// <see cref="Replace"/> is a COURTESY check, not the safety mechanism: it
/// keeps the ugly outcome below rare. Safety against writers that bypass the
/// lock protocol (a human shell, Sync delivering a remote edit mid-flight)
/// comes from the commit being an EXCHANGE — see <see cref="Replace"/>.</para>
/// </summary>
public static class AtomicFile
{
    /// <summary>Hidden + Sync-ignored + gitignored + unaddressable through the resolver.</summary>
    public const string TempPrefix = ".knapper-tmp-";

    /// <summary>
    /// Test seams standing in for an external writer (or a topology swap) at
    /// the instants no second process can hit on demand: inside
    /// <see cref="Replace"/> between the final SHA check and the exchange,
    /// and inside <see cref="CreateNew"/> between the temp write and the
    /// link. Both receive the target's absolute path; a test filters on its
    /// own vault's path, because these are static and test classes run in
    /// parallel. Like <c>KNAPPER_FAULT_SHORT_WRITE</c> these are seams for
    /// ADDING interference, not for skipping checks — there is no way to use
    /// either to land unverified bytes as success. Never set outside tests.
    /// </summary>
    internal static Action<string>? BeforeExchangeTestHook;
    internal static Action<string>? BeforeCreateLinkTestHook;

    /// <summary>
    /// The third seam: inside <see cref="UndoRacedExchange"/>, after a raced
    /// commit has been detected and before the swap-back — the only instant
    /// at which a THIRD writer (or a delete) can turn a clean rejection into
    /// the visible-recovery fallback. Same rules as the hooks above.
    /// </summary>
    internal static Action<string>? AfterRacedExchangeTestHook;

    /// <summary>
    /// When a raced replace displaces an external version that cannot be put
    /// back at the canonical pathname, the surviving bytes are republished
    /// under this key in the thrown exception's <see cref="Exception.Data"/>
    /// (value: the published file's NAME). The mutation service turns it into
    /// an audit detail and a generation bump — the durable reconciliation
    /// pointer a lost MCP response would otherwise take with it.
    /// </summary>
    public const string RecoveredPathDataKey = "knapper.recoveredPath";

    /// <summary>
    /// Atomically replace an existing file's bytes, preserving its mode.
    /// Throws <see cref="VaultErrorCode.PreconditionFailed"/> if the bytes on
    /// disk no longer hash to <paramref name="expectedSha256"/>.
    ///
    /// <para>The commit is an atomic EXCHANGE (renameat2 RENAME_EXCHANGE /
    /// renamex_np RENAME_SWAP), never an overwriting rename. An overwriting
    /// rename destroys whatever the target holds at the instant of the
    /// syscall, and the SHA check guarding it expired a syscall earlier — an
    /// external replacement landing in that gap was silently lost while the
    /// mutation reported success. The exchange swaps the target's current
    /// content to the hidden temp, and only THEN — against a private name no
    /// other writer can reach, and never through a symlink — decides whether
    /// what it displaced was the authorized base.</para>
    ///
    /// <para>If it was not, the commit raced an external write, and brief §7
    /// is explicit: reject stale input WITHOUT MUTATING. So the exchange is
    /// immediately exchanged BACK — the external bytes return to the
    /// canonical pathname, our own unacknowledged bytes come home to the temp
    /// (ours to discard), and the caller gets a clean typed rejection over an
    /// unchanged file. Only when the swap-back cannot restore the external
    /// version — a third write or a delete landed in the microseconds between
    /// the two exchanges — does the fallback engage: the surviving version is
    /// republished VISIBLY as a `(Knapper displaced …)` conflict sibling that
    /// Sync carries, queries see, and the conflict gate blocks on until a
    /// human reconciles. A hidden-only surviving version is not an acceptable
    /// state in any outcome (review follow-up, 2026-08-20).</para>
    ///
    /// <para>The honest residual: a process death between the two exchanges
    /// leaves the raced state as CRASH residue — stale bytes canonical,
    /// external bytes hidden — with no receipt issued. The window is one
    /// lstat plus a read-and-hash of the displaced bytes (page-cached in the
    /// common case, bounded by the sync ceiling) wide, plus the undo's
    /// syscalls when actually raced; reaching it at all requires an external
    /// write within a syscall of the final check. It is deliberately NOT
    /// narrowed by a metadata fast path — round four demonstrated that an
    /// equal stat tuple does not prove equal bytes, and the precondition
    /// that prevents silent loss outranks the width of this window. It
    /// cannot be closed with these primitives — closing OR narrowing it
    /// needs durable recovery metadata and a startup pass, which this design
    /// deliberately does not have. `CrashDurabilityTests` DEMONSTRATES the
    /// state with a real killed process so the vault owner's risk decision
    /// rests on evidence, not on this comment.</para>
    /// </summary>
    public static void Replace(string absolutePath, byte[] data, string expectedSha256)
    {
        var directory = Path.GetDirectoryName(absolutePath)!;

        // Non-following, and typed: a target that has become a symlink or a
        // FIFO/socket/device since Resolve is an external swap — refuse
        // before reading (a read through a symlink judges some other file;
        // a read of a FIFO blocks forever while holding the path locks).
        var before = Posix.LStat(absolutePath);
        if (before.IsSymlink)
        {
            throw new KnapperException(VaultErrorCode.SymlinkRejected,
                $"{absolutePath} was replaced by a symlink after it was checked — refused; nothing was written");
        }
        if (!before.IsRegular)
        {
            throw new KnapperException(VaultErrorCode.PreconditionFailed,
                $"{absolutePath} is no longer a regular file (an external writer replaced it with a " +
                "FIFO, socket, or device) — refused; nothing was written");
        }

        // Courtesy check: catching a stale base HERE means nothing has been
        // exchanged yet, so the failure is clean. The exchange below is what
        // makes the window between this read and the commit safe.
        var latest = File.ReadAllBytes(absolutePath);
        if (!VaultHash.Matches(expectedSha256, latest))
        {
            throw new KnapperException(VaultErrorCode.PreconditionFailed,
                "precondition failed while the write was being prepared — the file changed; " +
                $"re-read and rebuild against current content (expected {expectedSha256}, found {VaultHash.Sha256Hex(latest)})");
        }

        var written = MaybeInjectShortWrite(absolutePath, data);
        var temp = WriteTemp(directory, written, before.Permissions);
        // Our temp's IDENTITY, recorded before any other writer can have
        // seen its name: the reclaim decision after a swap-back compares
        // device+inode against this, never bytes — byte equality is not
        // ownership, and a third-party write that happens to carry our bytes
        // must still be treated as theirs (review round three).
        var ourTemp = Posix.LStat(temp);
        var keepTemp = false;
        try
        {
            BeforeExchangeTestHook?.Invoke(absolutePath);
            try
            {
                Posix.Exchange(temp, absolutePath);
            }
            catch (KnapperException e) when (e.Code == VaultErrorCode.NotFound)
            {
                // The target vanished between the check and the commit. The
                // old overwriting rename would have silently resurrected it
                // with our bytes; the exchange refuses, and the honest answer
                // is the same "re-read and decide" a changed file gets.
                throw new KnapperException(VaultErrorCode.PreconditionFailed,
                    $"{absolutePath} was deleted by an external writer while the write was being " +
                    "prepared — nothing was written; re-read and decide against current state", e);
            }

            // From this instant the displaced external object exists only at
            // the hidden temp, so the default flips to RETAIN: every branch
            // below — including an exception nobody anticipated — keeps the
            // temp unless a step positively proves it is safe to discard
            // (authorized base, our own inode reclaimed, or the displaced
            // object given another pathname). Round three demonstrated the
            // opposite default deleting the only copy when an inspection
            // threw.
            keepTemp = true;

            if (DisplacedWasTheAuthorizedBase(temp, expectedSha256))
            {
                keepTemp = false; // proven: discarding it is the commit's contract
                return;
            }
            UndoRacedExchange(absolutePath, directory, temp, ourTemp, written, ref keepTemp); // always throws
        }
        finally
        {
            if (!keepTemp)
                TryDelete(temp);
            Posix.FsyncDirectory(directory);
        }
    }

    /// <summary>
    /// Judge the displaced object under the private name, never through a
    /// symlink. The ONE proof that it was the base the caller authorized is
    /// its BYTES: a regular file hashing to the expected base is
    /// byte-identical to what the caller was authorized to consume, so
    /// discarding it is a legal serialization (their identical write, then
    /// our conditional replace). There is deliberately NO metadata fast
    /// path: a dev/inode/size/mtime tuple equal to the pre-exchange stat is
    /// not proof of unchanged content — an in-place writer can preserve all
    /// four (mtime is user-settable, mtime-faithful sync tooling restores it
    /// as a matter of course, and filesystem timestamp granularity aliases
    /// back-to-back writes with no help), and ctime, the one field that
    /// would notice, is bumped by the exchange itself — so no stat tuple can
    /// ever carry this proof (round four: a stamp fast path here silently
    /// destroyed a raced in-place write while reporting success). Anything
    /// else — different bytes, a symlink, a non-regular file, an UNREADABLE
    /// file — answers "not the base", which routes to the swap-back: the
    /// keep direction. An unreadable-but-untouched base therefore ends in a
    /// spurious but SAFE PreconditionFailed over an unchanged file, never a
    /// success the hash could not verify.
    /// </summary>
    private static bool DisplacedWasTheAuthorizedBase(string temp, string expectedSha256)
    {
        try
        {
            var displaced = Posix.LStat(temp);
            return displaced.IsRegular && VaultHash.Matches(expectedSha256, File.ReadAllBytes(temp));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>
    /// The raced commit's undo. On entry: OUR bytes sit at the canonical
    /// pathname (a stale edit no client has been told succeeded), the
    /// external writer's version sits at the hidden temp, and
    /// <paramref name="keepTemp"/> is already TRUE — retention is the
    /// default, and each branch below clears it only on positive proof.
    /// Every exit throws <see cref="VaultErrorCode.PreconditionFailed"/>;
    /// the work here is deciding WHERE the external bytes end up, in strict
    /// preference order: back at the canonical pathname by swap (clean
    /// rejection, file untouched net) → restored to the canonical name by
    /// no-follow link when the name went free → republished as a visible
    /// conflict sibling → kept hidden only when even that link fails, named
    /// in the error. Links are NO-FOLLOW throughout: the displaced object
    /// can be a symlink, and macOS link(2) would otherwise "restore" it as
    /// a hard link to its out-of-vault target (review round three).
    /// </summary>
    private static void UndoRacedExchange(
        string absolutePath, string directory, string temp, Posix.StatInfo ourTemp, byte[] written,
        ref bool keepTemp)
    {
        AfterRacedExchangeTestHook?.Invoke(absolutePath);
        try
        {
            Posix.Exchange(temp, absolutePath);
        }
        catch (Exception undo) when (undo is not OutOfMemoryException)
        {
            // The canonical name no longer holds a file to swap with (their
            // delete took our just-landed bytes — which stay deleted; they
            // were never acknowledged) or the swap failed outright. Restore
            // the external version by no-clobber, no-follow link if the name
            // is free.
            try
            {
                Posix.LinkNoFollow(temp, absolutePath);
                Posix.FsyncDirectory(directory);
                // Restored: the canonical name holds the displaced object
                // again, so the temp name is now a mere duplicate link.
                keepTemp = false;
                throw new KnapperException(VaultErrorCode.PreconditionFailed,
                    "the commit raced an external writer and was rolled back — the file holds their " +
                    "content again; re-read and rebuild against it", undo);
            }
            catch (KnapperException e) when (e.Code != VaultErrorCode.PreconditionFailed)
            {
                // Name retaken or link refused: publish the survivor visibly.
                throw RecoveredSiblingFailure(absolutePath, directory, temp, ref keepTemp, e);
            }
        }
        Posix.FsyncDirectory(directory);

        // Swapped back. The reclaim decision leads with IDENTITY, never
        // bytes alone: only the inode this call created (whose hidden name
        // no other writer ever learned) may be discarded, so a third write
        // that happens to carry byte-identical content is still a distinct
        // inode and still theirs — it becomes the visible displaced sibling.
        // The byte comparison is a SECOND requirement, not an alternative:
        // ext4 can hand a just-freed inode number straight back to the next
        // create, so identity alone could bless a stranger; demanding both
        // makes every disagreement a KEEP. Anything unreadable stays
        // retained.
        bool reclaimedOurs;
        try
        {
            reclaimedOurs = Posix.LStat(temp).SameIdentity(ourTemp)
                && File.ReadAllBytes(temp).AsSpan().SequenceEqual(written);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            reclaimedOurs = false;
        }
        if (reclaimedOurs)
        {
            keepTemp = false; // proven ours by inode; discarding our own unacknowledged bytes
            throw new KnapperException(VaultErrorCode.PreconditionFailed,
                "precondition failed at the commit — an external writer replaced the file in the final " +
                "instant, and the write was rolled back; the file holds their content. Re-read and " +
                "rebuild against current content; NEVER retry with the old base.");
        }
        throw RecoveredSiblingFailure(absolutePath, directory, temp, ref keepTemp, cause: null);
    }

    /// <summary>
    /// Last resort short of hidden-only: hard-link the surviving external
    /// version to a visible `(Knapper displaced …)` sibling — Sync carries
    /// it, queries see it, and the conflict gate blocks the note until a
    /// human reconciles (`ConflictDetector.DisplacedMarker`). NO-FOLLOW, so
    /// a displaced symlink is published as the symlink it is (then it is a
    /// filesystem-visible recovery object rather than syncable content —
    /// the conflict walk still lists it by name). Only if even this link
    /// fails does the hidden temp survive, named in the error.
    /// </summary>
    private static KnapperException RecoveredSiblingFailure(
        string absolutePath, string directory, string temp, ref bool keepTemp, Exception? cause)
    {
        var recovered = Path.GetFileNameWithoutExtension(absolutePath)
            + Mutation.ConflictDetector.DisplacedMarker
            + $" {DateTime.UtcNow:yyyy-MM-dd HH-mm-ss} {Guid.NewGuid().ToString("N")[..8]})"
            + Path.GetExtension(absolutePath);
        string? published = null;
        try
        {
            Posix.LinkNoFollow(temp, Path.Combine(directory, recovered));
            Posix.FsyncDirectory(directory);
            published = recovered;
            keepTemp = false; // the displaced object now has a visible pathname of its own
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            keepTemp = true;
            cause ??= e;
        }

        var ke = new KnapperException(VaultErrorCode.PreconditionFailed,
            published is not null
                ? "the commit raced more than one external write, and the displaced external version " +
                  $"could not be restored to the note's own pathname. It was preserved VISIBLY at " +
                  $"'{published}' — a conflict sibling: mutations to this note are blocked until a human " +
                  "reconciles the two versions. Re-read the note to see what is at its pathname now."
                : "the commit raced external writes and the displaced external version could not be " +
                  $"republished; it survives at '{Path.GetFileName(temp)}' (hidden — a human must place " +
                  "it). Re-read the note to see what is at its pathname now.",
            cause);
        ke.Data[RecoveredPathDataKey] = published ?? Path.GetFileName(temp);
        return ke;
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
            BeforeCreateLinkTestHook?.Invoke(absolutePath);
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
    /// Inherent limit: a ZERO-byte write cannot be shortened, so the
    /// injector is inert for empty content — there is no short-write
    /// failure mode for zero bytes to simulate.
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
