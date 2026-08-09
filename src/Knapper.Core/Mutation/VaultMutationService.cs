using System.Text;
using Knapper.Core.Generation;
using Knapper.Core.Interop;
using Knapper.Core.Locking;
using Knapper.Core.Options;
using Knapper.Core.Vault;

namespace Knapper.Core.Mutation;

/// <summary>
/// The transaction layer (brief §7) — the C# port of
/// <c>vault-edit.reference.py</c>'s semantics, and the ONLY mutation surface
/// this codebase will ever expose. There is no unconditional write anywhere:
/// every operation on an existing file demands <c>expect_sha256</c>, every
/// critical section runs the exact order
/// lock → fresh read → SHA check → transform → validate guards →
/// hidden temp + fsync → final SHA check → atomic replace →
/// reopen and byte-compare → unlock,
/// and every attempt — including rejections — lands in the audit log.
/// </summary>
public sealed class VaultMutationService(
    VaultPathResolver resolver,
    VaultLockManager locks,
    VaultGenerationCounter generation,
    ConflictDetector conflicts,
    ISyncGate syncGate,
    VaultOptions options,
    AuditLog? audit = null)
{
    private TimeSpan LockTimeout => TimeSpan.FromMilliseconds(options.LockTimeoutMs);

    /// <summary>
    /// Test seams for the move/delete link–unlink window. The per-path lock
    /// only binds cooperating Knapper writers — Sync and human shells honor
    /// no locks — so the external-writer race cannot be reproduced from a
    /// second process on demand; these run inside the critical section
    /// immediately before/after the destination hard link is created, and a
    /// test uses them to stand in for that external writer. Never set
    /// outside tests.
    /// </summary>
    internal Action<string>? BeforeLinkTestHook;
    internal Action<string>? AfterLinkTestHook;

    // ---- vault_edit ----------------------------------------------------

    public MutationResult Edit(
        string path,
        string expectSha256,
        IReadOnlyList<EditSpec> edits,
        IReadOnlyList<string>? guards = null,
        AuditContext? ctx = null)
    {
        ValidateEdits(edits);
        var guardList = ValidateGuards(guards);
        return Mutate("edit", path, expectSha256, ctx, vp =>
        {
            var data = ReadExisting(vp);
            RequireSha(expectSha256, data, vp);
            var text = DecodeUtf8(data, vp);

            foreach (var guard in guardList.Where(g => !text.Contains(g, StringComparison.Ordinal)))
            {
                throw new KnapperException(VaultErrorCode.GuardViolation,
                    $"guard not present before edit — wrong file or stale assumptions: '{Snip(guard)}'");
            }

            var newText = text;
            for (var i = 0; i < edits.Count; i++)
            {
                var edit = edits[i];
                var found = CountOccurrences(newText, edit.Old);
                if (found != edit.Count)
                {
                    throw new KnapperException(VaultErrorCode.AnchorMismatch,
                        $"edit[{i}]: anchor matched {found} times, expected exactly {edit.Count} — " +
                        $"file untouched: '{Snip(edit.Old)}'");
                }
                newText = newText.Replace(edit.Old, edit.New, StringComparison.Ordinal);
            }
            if (newText == text)
                throw new KnapperException(VaultErrorCode.InvalidArgument, "edits produced no change");
            foreach (var guard in guardList.Where(g => !newText.Contains(g, StringComparison.Ordinal)))
            {
                throw new KnapperException(VaultErrorCode.GuardViolation,
                    $"guard would not survive the edit — file untouched: '{Snip(guard)}'");
            }

            return (data, Encoding.UTF8.GetBytes(newText));
        });
    }

    // ---- vault_append --------------------------------------------------

    public MutationResult Append(string path, string expectSha256, string text, AuditContext? ctx = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new KnapperException(VaultErrorCode.InvalidArgument,
                "text is required and must be non-empty (include a leading newline yourself if needed)");
        }
        return Mutate("append", path, expectSha256, ctx, vp =>
        {
            var data = ReadExisting(vp);
            RequireSha(expectSha256, data, vp);
            _ = DecodeUtf8(data, vp); // append only to text files
            return (data, [.. data, .. Encoding.UTF8.GetBytes(text)]);
        });
    }

    // ---- vault_create --------------------------------------------------

    public MutationResult Create(string path, string text, AuditContext? ctx = null)
    {
        var vp = resolver.Resolve(path);
        try
        {
            conflicts.AssertNotConflicted(vp);
            syncGate.AssertMutationsAllowed();
            var written = Encoding.UTF8.GetBytes(text);
            using (locks.AcquirePathLock(vp, LockTimeout))
            {
                RequireAuditIntent("create", vp.Relative, ctx);
                AtomicFile.CreateNew(vp.Absolute, written);
                AtomicFile.VerifyOnDisk(vp.Absolute, written);
            }
            var gen = generation.Increment();
            var sha = VaultHash.Sha256Hex(written);
            TryAudit("create", vp.Relative, "ok", ctx, before: null, after: sha);
            return new MutationResult(vp.Relative, null, sha, 0, written.Length, true, gen);
        }
        catch (Exception e) when (e is KnapperException or IOException or UnauthorizedAccessException)
        {
            var ke = NormalizeIo(e, "create", vp.Relative);
            TryAudit("create", vp.Relative, ke.Code.ToString(), ctx);
            if (ReferenceEquals(ke, e))
                throw;
            throw ke;
        }
    }

    /// <summary>
    /// Folder creation is a deliberate, explicit act (never implied by a file
    /// create). One level at a time: the parent must already exist.
    /// </summary>
    public void CreateDirectory(string path, AuditContext? ctx = null)
    {
        var vp = resolver.Resolve(path);
        try
        {
            syncGate.AssertMutationsAllowed();
            if (Directory.Exists(vp.Absolute) || File.Exists(vp.Absolute))
                throw new KnapperException(VaultErrorCode.AlreadyExists, $"already exists: {vp.Relative}");
            var parent = Path.GetDirectoryName(vp.Absolute)!;
            if (!Directory.Exists(parent))
            {
                throw new KnapperException(VaultErrorCode.NotFound,
                    $"parent directory does not exist: create it first (one deliberate level at a time)");
            }
            RequireAuditIntent("mkdir", vp.Relative, ctx);
            Directory.CreateDirectory(vp.Absolute);
            generation.Increment();
            TryAudit("mkdir", vp.Relative, "ok", ctx);
        }
        catch (Exception e) when (e is KnapperException or IOException or UnauthorizedAccessException)
        {
            var ke = NormalizeIo(e, "mkdir", vp.Relative);
            TryAudit("mkdir", vp.Relative, ke.Code.ToString(), ctx);
            if (ReferenceEquals(ke, e))
                throw;
            throw ke;
        }
    }

    // ---- move ----------------------------------------------------------

    public MutationResult Move(string sourcePath, string destinationPath, string expectSourceSha256, AuditContext? ctx = null)
    {
        var source = resolver.Resolve(sourcePath);
        var destination = resolver.Resolve(destinationPath);
        try
        {
            if (source.Relative == destination.Relative)
                throw new KnapperException(VaultErrorCode.InvalidArgument, "source and destination are the same path");
            conflicts.AssertNotConflicted(source);
            conflicts.AssertNotConflicted(destination);
            syncGate.AssertMutationsAllowed();

            using (locks.AcquirePathLocks([source, destination], LockTimeout))
            {
                var data = ReadExisting(source);
                RequireSha(expectSourceSha256, data, source);
                if (File.Exists(destination.Absolute) || Directory.Exists(destination.Absolute))
                    throw new KnapperException(VaultErrorCode.AlreadyExists, $"destination already exists: {destination.Relative}");
                var destinationDir = Path.GetDirectoryName(destination.Absolute)!;
                if (!Directory.Exists(destinationDir))
                {
                    throw new KnapperException(VaultErrorCode.NotFound,
                        $"destination directory does not exist: {Path.GetDirectoryName(destination.Relative)} " +
                        "(folder creation is a deliberate act — do it explicitly first)");
                }

                // link-then-unlink, not rename: rename(2) silently replaces an
                // existing destination; link(2) cannot. Same inode → no data
                // copy, mode/mtime ride along, content can't diverge mid-move.
                RequireAuditIntent("move", source.Relative, ctx);
                BeforeLinkTestHook?.Invoke(source.Absolute);
                Posix.Link(source.Absolute, destination.Absolute);
                var sourceUnlinked = false;
                try
                {
                    AfterLinkTestHook?.Invoke(source.Absolute);
                    Posix.FsyncDirectory(destinationDir);
                    // Also the freshness check for the read→link window: if an
                    // external writer (Sync, a human shell — nothing that
                    // honors our locks) replaced the source before the link,
                    // the linked inode carries THEIR bytes and this fails —
                    // the stray link is rolled back below.
                    AtomicFile.VerifyOnDisk(destination.Absolute, data);
                    // And the link→unlink window: unlinking must not destroy
                    // an external replacement that landed after the link.
                    RequireStillOurBytes(source, data,
                        "changed during the move — move rolled back, nothing was removed");
                    File.Delete(source.Absolute);
                    sourceUnlinked = true;
                    Posix.FsyncDirectory(Path.GetDirectoryName(source.Absolute)!);
                }
                catch when (!sourceUnlinked)
                {
                    // The move did not complete and the source still exists
                    // (possibly as an external writer's replacement): a failed
                    // operation must leave no new pathname behind.
                    TryRemoveStrayLink(destination.Absolute, destinationDir);
                    throw;
                }

                var gen = generation.Increment();
                var sha = VaultHash.Sha256Hex(data);
                TryAudit("move", source.Relative, "ok", ctx, before: sha, after: sha,
                    detail: "→ " + destination.Relative);
                return new MutationResult(destination.Relative, sha, sha, data.Length, data.Length, true, gen);
            }
        }
        catch (Exception e) when (e is KnapperException or IOException or UnauthorizedAccessException)
        {
            var ke = NormalizeIo(e, "move", source.Relative);
            TryAudit("move", source.Relative, ke.Code.ToString(), ctx, null, null,
                "→ " + destination.Relative);
            if (ReferenceEquals(ke, e))
                throw;
            throw ke;
        }
    }

    // ---- soft delete ---------------------------------------------------

    public DeleteResult Delete(string path, string expectSha256, AuditContext? ctx = null)
    {
        var vp = resolver.Resolve(path);
        try
        {
            conflicts.AssertNotConflicted(vp);
            syncGate.AssertMutationsAllowed();

            using (locks.AcquirePathLock(vp, LockTimeout))
            {
                var data = ReadExisting(vp);
                RequireSha(expectSha256, data, vp);

                // Trash paths are built from the ALREADY-VALIDATED relative
                // path — .trash/ is deliberately unreachable via the resolver.
                var trashRelative = ".trash/" + vp.Relative;
                var trashAbsolute = Path.Combine(resolver.Root, trashRelative);
                if (File.Exists(trashAbsolute))
                {
                    // A previous soft delete of the same path: keep both.
                    var stamped = Path.GetFileNameWithoutExtension(trashAbsolute)
                        + $"-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}"
                        + Path.GetExtension(trashAbsolute);
                    trashRelative = ".trash/"
                        + (Path.GetDirectoryName(vp.Relative) is { Length: > 0 } parent ? parent + "/" : "")
                        + stamped;
                    trashAbsolute = Path.Combine(resolver.Root, trashRelative);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(trashAbsolute)!);

                var trashDir = Path.GetDirectoryName(trashAbsolute)!;
                RequireAuditIntent("delete", vp.Relative, ctx);
                BeforeLinkTestHook?.Invoke(vp.Absolute);
                Posix.Link(vp.Absolute, trashAbsolute);
                var sourceUnlinked = false;
                try
                {
                    AfterLinkTestHook?.Invoke(vp.Absolute);
                    Posix.FsyncDirectory(trashDir);
                    // Same two-window protection as move: a verify failure
                    // here means an external writer replaced the file before
                    // the link (the trash link holds THEIR bytes)...
                    AtomicFile.VerifyOnDisk(trashAbsolute, data);
                    // ...and this one means they replaced it after — either
                    // way the delete is stale and must not remove their write.
                    RequireStillOurBytes(vp, data,
                        "changed during the delete — delete rolled back, nothing was removed");
                    File.Delete(vp.Absolute);
                    sourceUnlinked = true;
                    Posix.FsyncDirectory(Path.GetDirectoryName(vp.Absolute)!);
                }
                catch when (!sourceUnlinked)
                {
                    // Failed delete leaves no new .trash entry behind.
                    TryRemoveStrayLink(trashAbsolute, trashDir);
                    throw;
                }

                var gen = generation.Increment();
                var sha = VaultHash.Sha256Hex(data);
                TryAudit("delete", vp.Relative, "ok", ctx, before: sha, after: null, detail: "→ " + trashRelative);
                return new DeleteResult(vp.Relative, trashRelative, sha, gen);
            }
        }
        catch (Exception e) when (e is KnapperException or IOException or UnauthorizedAccessException)
        {
            var ke = NormalizeIo(e, "delete", vp.Relative);
            TryAudit("delete", vp.Relative, ke.Code.ToString(), ctx);
            if (ReferenceEquals(ke, e))
                throw;
            throw ke;
        }
    }

    // ---- batch ---------------------------------------------------------

    /// <summary>
    /// Locks all paths in sorted order, validates EVERY item's preconditions,
    /// anchors, and guards before the first write (brief §7), then applies.
    /// Not cross-file atomic: an apply-phase I/O failure stops the batch and
    /// reports Applied/Failed/NotAttempted per item — git history is the
    /// recovery path.
    /// </summary>
    public BatchResult Batch(IReadOnlyList<BatchItem> items, AuditContext? ctx = null)
    {
        if (items.Count == 0)
            throw new KnapperException(VaultErrorCode.InvalidArgument, "batch is empty");
        if (items.Count > options.MaxBatchItems)
        {
            throw new KnapperException(VaultErrorCode.InvalidArgument,
                $"batch has {items.Count} items; the cap is {options.MaxBatchItems}");
        }

        var resolved = items.Select(i => resolver.Resolve(i.Path)).ToList();
        foreach (var vp in resolved)
            conflicts.AssertNotConflicted(vp);
        syncGate.AssertMutationsAllowed();

        using (locks.AcquirePathLocks(resolved, LockTimeout)) // rejects duplicate paths
        {
            // Validate phase: plan every write. Any failure here = nothing mutated.
            var plans = new List<(VaultPath Vp, byte[]? Before, byte[] After, BatchItem Item)>();
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var vp = resolved[i];
                try
                {
                    plans.Add(item.Kind switch
                    {
                        BatchItemKind.Edit => PlanEdit(vp, item),
                        BatchItemKind.Append => PlanAppend(vp, item),
                        BatchItemKind.Create => PlanCreate(vp, item),
                        _ => throw new KnapperException(VaultErrorCode.InvalidArgument, $"unknown batch kind {item.Kind}"),
                    });
                }
                catch (Exception e) when (e is KnapperException or IOException or UnauthorizedAccessException)
                {
                    var ke = NormalizeIo(e, "batch-validate", vp.Relative);
                    TryAudit("batch-validate", vp.Relative, ke.Code.ToString(), ctx);
                    throw new KnapperException(ke.Code,
                        $"batch item {i} ({vp.Relative}) failed validation — nothing was mutated: {ke.Message}", ke);
                }
            }

            // Apply phase.
            var results = new List<BatchItemResult>(items.Count);
            var failedAt = -1;
            for (var i = 0; i < plans.Count; i++)
            {
                var (vp, before, after, item) = plans[i];
                if (failedAt >= 0)
                {
                    results.Add(new BatchItemResult(vp.Relative, BatchItemStatus.NotAttempted, null, null, null));
                    continue;
                }
                var opName = "batch-" + item.Kind.ToString().ToLowerInvariant();
                try
                {
                    // Per-item intent: if the audit sink dies mid-batch, the
                    // NEXT item is refused before its write (this catch turns
                    // that into Failed + NotAttempted) — items already landed
                    // keep both their audit records and their receipt.
                    RequireAuditIntent(opName, vp.Relative, ctx);
                    if (item.Kind == BatchItemKind.Create)
                        AtomicFile.CreateNew(vp.Absolute, after);
                    else
                        AtomicFile.Replace(vp.Absolute, after, VaultHash.Sha256Hex(before!));
                    AtomicFile.VerifyOnDisk(vp.Absolute, after);
                    generation.Increment();
                    var sha = VaultHash.Sha256Hex(after);
                    TryAudit(opName, vp.Relative, "ok", ctx,
                        before is null ? null : VaultHash.Sha256Hex(before), sha);
                    results.Add(new BatchItemResult(vp.Relative, BatchItemStatus.Applied, sha, null, null));
                }
                catch (Exception e) when (e is KnapperException or IOException or UnauthorizedAccessException)
                {
                    // A raw I/O failure mid-apply must NOT abort the MCP call:
                    // the caller is owed the Applied/Failed/NotAttempted
                    // receipt for the items that already landed.
                    var ke = NormalizeIo(e, "batch-apply", vp.Relative);
                    TryAudit(opName, vp.Relative, ke.Code.ToString(), ctx);
                    results.Add(new BatchItemResult(vp.Relative, BatchItemStatus.Failed, null, ke.Code, ke.Message));
                    failedAt = i;
                }
            }
            return new BatchResult(results, results.All(r => r.Status == BatchItemStatus.Applied));
        }
    }

    private (VaultPath, byte[]?, byte[], BatchItem) PlanEdit(VaultPath vp, BatchItem item)
    {
        var edits = item.Edits ?? throw new KnapperException(VaultErrorCode.InvalidArgument, "edit item requires edits[]");
        ValidateEdits(edits);
        var guards = ValidateGuards(item.Guards);
        var data = ReadExisting(vp);
        RequireSha(RequireField(item.ExpectSha256, "expect_sha256"), data, vp);
        var text = DecodeUtf8(data, vp);
        foreach (var guard in guards.Where(g => !text.Contains(g, StringComparison.Ordinal)))
            throw new KnapperException(VaultErrorCode.GuardViolation, $"guard not present: '{Snip(guard)}'");
        var newText = text;
        for (var i = 0; i < edits.Count; i++)
        {
            var found = CountOccurrences(newText, edits[i].Old);
            if (found != edits[i].Count)
            {
                throw new KnapperException(VaultErrorCode.AnchorMismatch,
                    $"edit[{i}]: anchor matched {found} times, expected exactly {edits[i].Count}: '{Snip(edits[i].Old)}'");
            }
            newText = newText.Replace(edits[i].Old, edits[i].New, StringComparison.Ordinal);
        }
        if (newText == text)
            throw new KnapperException(VaultErrorCode.InvalidArgument, "edits produced no change");
        foreach (var guard in guards.Where(g => !newText.Contains(g, StringComparison.Ordinal)))
            throw new KnapperException(VaultErrorCode.GuardViolation, $"guard would not survive: '{Snip(guard)}'");
        return (vp, data, Encoding.UTF8.GetBytes(newText), item);
    }

    private (VaultPath, byte[]?, byte[], BatchItem) PlanAppend(VaultPath vp, BatchItem item)
    {
        if (string.IsNullOrEmpty(item.Text))
            throw new KnapperException(VaultErrorCode.InvalidArgument, "append item requires non-empty text");
        var data = ReadExisting(vp);
        RequireSha(RequireField(item.ExpectSha256, "expect_sha256"), data, vp);
        _ = DecodeUtf8(data, vp);
        return (vp, data, [.. data, .. Encoding.UTF8.GetBytes(item.Text)], item);
    }

    private (VaultPath, byte[]?, byte[], BatchItem) PlanCreate(VaultPath vp, BatchItem item)
    {
        if (item.Text is null)
            throw new KnapperException(VaultErrorCode.InvalidArgument, "create item requires text (may be empty)");
        if (File.Exists(vp.Absolute) || Directory.Exists(vp.Absolute))
            throw new KnapperException(VaultErrorCode.AlreadyExists, $"already exists: {vp.Relative}");
        if (!Directory.Exists(Path.GetDirectoryName(vp.Absolute)!))
            throw new KnapperException(VaultErrorCode.NotFound, $"parent directory does not exist for {vp.Relative}");
        return (vp, null, Encoding.UTF8.GetBytes(item.Text), item);
    }

    // ---- the shared critical section -----------------------------------

    private MutationResult Mutate(
        string op, string path, string expectSha256, AuditContext? ctx,
        Func<VaultPath, (byte[] Before, byte[] After)> transform)
    {
        var vp = resolver.Resolve(path);
        try
        {
            conflicts.AssertNotConflicted(vp);
            syncGate.AssertMutationsAllowed();

            using (locks.AcquirePathLock(vp, LockTimeout))
            {
                var (before, after) = transform(vp);
                var beforeSha = VaultHash.Sha256Hex(before);
                RequireAuditIntent(op, vp.Relative, ctx);
                AtomicFile.Replace(vp.Absolute, after, beforeSha);
                AtomicFile.VerifyOnDisk(vp.Absolute, after);
                var gen = generation.Increment();
                var afterSha = VaultHash.Sha256Hex(after);
                TryAudit(op, vp.Relative, "ok", ctx, beforeSha, afterSha);
                return new MutationResult(vp.Relative, beforeSha, afterSha, before.Length, after.Length, true, gen);
            }
        }
        catch (Exception e) when (e is KnapperException or IOException or UnauthorizedAccessException)
        {
            // Rejections are audited too — a stale-write rejection is signal.
            var ke = NormalizeIo(e, op, vp.Relative);
            TryAudit(op, vp.Relative, ke.Code.ToString(), ctx);
            if (ReferenceEquals(ke, e))
                throw;
            throw ke;
        }
    }

    // ---- helpers -------------------------------------------------------

    /// <summary>
    /// Nothing above Core should need to catch raw <see cref="IOException"/>
    /// to know what went wrong: filesystem/OS failures normalize to a typed
    /// IoError at each operation boundary, so the failure is audited and
    /// reaches the client with a stable bracketed code instead of aborting
    /// the MCP call shapeless. OS messages carry paths, never note content.
    /// </summary>
    private static KnapperException NormalizeIo(Exception e, string op, string relative) =>
        e as KnapperException ?? new KnapperException(VaultErrorCode.IoError,
            $"filesystem failure during {op} on {relative}: {e.Message}", e);

    /// <summary>
    /// The link→unlink freshness check, by content (house rule: verification
    /// is by content, never by receipt — and byte-comparing sidesteps
    /// platform-specific stat interop for inode identity). An irreducible
    /// window remains between this read and the unlink; it is vastly
    /// narrower than read→unlink and carries no lost-update risk beyond
    /// what rename-based moves would have.
    /// </summary>
    private static void RequireStillOurBytes(VaultPath vp, byte[] expected, string whatHappened)
    {
        bool same;
        try
        {
            same = File.Exists(vp.Absolute) && File.ReadAllBytes(vp.Absolute).AsSpan().SequenceEqual(expected);
        }
        catch (IOException)
        {
            same = false;
        }
        if (!same)
        {
            throw new KnapperException(VaultErrorCode.PreconditionFailed,
                $"{vp.Relative} {whatHappened}; an external writer (Sync or a human) raced this " +
                "operation — re-read and retry against current content");
        }
    }

    /// <summary>
    /// Best-effort removal of a link created by an operation that then
    /// failed. Best-effort is acceptable: the primary error still propagates,
    /// and the same cleanup pattern guards AtomicFile's temp files.
    /// </summary>
    private static void TryRemoveStrayLink(string absolutePath, string directory)
    {
        try
        {
            File.Delete(absolutePath);
            Posix.FsyncDirectory(directory);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static byte[] ReadExisting(VaultPath vp)
    {
        if (Directory.Exists(vp.Absolute))
            throw new KnapperException(VaultErrorCode.InvalidArgument, $"path is a directory: {vp.Relative}");
        if (!File.Exists(vp.Absolute))
            throw new KnapperException(VaultErrorCode.NotFound, $"no such file: {vp.Relative}");
        return File.ReadAllBytes(vp.Absolute);
    }

    private static void RequireSha(string expected, byte[] data, VaultPath vp)
    {
        if (string.IsNullOrWhiteSpace(expected))
        {
            throw new KnapperException(VaultErrorCode.InvalidArgument,
                "expect_sha256 is required — read the file first and pass its sha256");
        }
        if (!VaultHash.Matches(expected, data))
        {
            throw new KnapperException(VaultErrorCode.PreconditionFailed,
                $"precondition failed: {vp.Relative} changed since your read — re-read and rebuild " +
                $"against current content; NEVER retry with the old base " +
                $"(expected {expected.Trim().ToLowerInvariant()}, current {VaultHash.Sha256Hex(data)})");
        }
    }

    private static string DecodeUtf8(byte[] data, VaultPath vp)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(data);
        }
        catch (DecoderFallbackException)
        {
            throw new KnapperException(VaultErrorCode.NotUtf8,
                $"{vp.Relative} is not valid UTF-8 text; text mutations refuse it");
        }
    }

    private static void ValidateEdits(IReadOnlyList<EditSpec> edits)
    {
        if (edits.Count == 0)
            throw new KnapperException(VaultErrorCode.InvalidArgument, "edits[] is required and must be non-empty");
        for (var i = 0; i < edits.Count; i++)
        {
            if (string.IsNullOrEmpty(edits[i].Old))
                throw new KnapperException(VaultErrorCode.InvalidArgument, $"edit[{i}]: 'old' must be non-empty");
            if (edits[i].Old == edits[i].New)
                throw new KnapperException(VaultErrorCode.InvalidArgument, $"edit[{i}]: old == new");
            if (edits[i].Count < 1)
                throw new KnapperException(VaultErrorCode.InvalidArgument, $"edit[{i}]: count must be >= 1");
        }
    }

    private static IReadOnlyList<string> ValidateGuards(IReadOnlyList<string>? guards)
    {
        var list = guards ?? [];
        if (list.Any(string.IsNullOrEmpty))
            throw new KnapperException(VaultErrorCode.InvalidArgument, "each guard must be a non-empty string");
        return list;
    }

    private static string RequireField(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new KnapperException(VaultErrorCode.InvalidArgument, $"{name} is required")
            : value;

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length; // non-overlapping, like str.count
        }
        return count;
    }

    private static string Snip(string s) => s.Length <= 120 ? s : s[..120] + "…";

    /// <summary>
    /// The audit log lives OUTSIDE the vault and vault content must never
    /// reach it (CLAUDE.md invariant). <paramref name="detail"/> therefore
    /// takes only path/checksum-derived strings — NEVER an exception
    /// message: anchor and guard failures embed up to 120 chars of note
    /// text in theirs. The error CODE is the audit signal; the
    /// content-bearing diagnostics stay on the immediate MCP response.
    /// </summary>
    private void Audit(
        string op, string relative, string outcome, AuditContext? ctx,
        string? before = null, string? after = null, string? detail = null)
    {
        audit?.Append(new AuditLog.Entry(
            DateTimeOffset.UtcNow, op, relative, outcome,
            ctx?.Client, ctx?.RequestId, before, after, detail));
    }

    /// <summary>
    /// The write-ahead half of the audit contract: an "attempt" record lands
    /// (fsynced) BEFORE the first byte of a mutation touches the vault, so
    /// no change can ever exist that no audit line explains. If the sink is
    /// down, the mutation is refused before any write — fail closed, file
    /// untouched — and the sink has already counted the failure into the
    /// durable metrics the external monitor watches.
    /// </summary>
    private void RequireAuditIntent(string op, string relative, AuditContext? ctx)
    {
        try
        {
            Audit(op, relative, "attempt", ctx);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new KnapperException(VaultErrorCode.IoError,
                $"audit log is unavailable — {op} of {relative} refused before any write (fail closed)", e);
        }
    }

    /// <summary>
    /// The post-write half: best-effort by design. The work it describes has
    /// already landed (or already produced its typed rejection) — a failing
    /// audit sink must not turn that into a false failure or destroy a batch
    /// receipt, and must NEVER be retried against the same failed sink from
    /// a catch path. The failure is counted durably at the sink; the
    /// "attempt" record from <see cref="RequireAuditIntent"/> still explains
    /// the change.
    /// </summary>
    private void TryAudit(
        string op, string relative, string outcome, AuditContext? ctx,
        string? before = null, string? after = null, string? detail = null)
    {
        try
        {
            Audit(op, relative, outcome, ctx, before, after, detail);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
