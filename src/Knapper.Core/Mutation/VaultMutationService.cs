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
    SyncOptions syncOptions,
    ArchivedPrefixes archived,
    AuditLog? audit = null)
{
    private TimeSpan LockTimeout => TimeSpan.FromMilliseconds(options.LockTimeoutMs);

    /// <summary>
    /// Refuse an operation that would CHANGE something already inside an
    /// archived subtree (<c>Vault:ArchivedPrefixes</c>).
    ///
    /// <para>Applied to edit, append, delete, and a move's SOURCE — never to
    /// create, mkdir, or a move's DESTINATION. That asymmetry is the whole
    /// design: an archive is FILLED by writing into it (a note is trimmed
    /// down and its superseded version filed), so a blanket ban under the
    /// prefix would ban the workflow the setting exists to serve. What is
    /// protected is what is already there.</para>
    ///
    /// <para>Called INSIDE the audited region of each operation, so a refusal
    /// lands in the trail like any other rejection — there is a real vault
    /// object here, unlike the resolver-gate refusals that deliberately
    /// precede auditing.</para>
    /// </summary>
    private void RequireNotArchived(VaultPath vp, string what)
    {
        if (!archived.Covers(vp.Relative))
            return;
        throw new KnapperException(VaultErrorCode.PathArchived,
            $"'{vp.Relative}' is in an archived subtree and holds a superseded copy: {what} is refused. " +
            "Creating and moving files INTO an archived prefix is allowed; changing what is already " +
            "there is a human action, in Obsidian.");
    }

    /// <summary>
    /// Refuse a write Obsidian Sync would silently strand. Measured against
    /// the POST-TRANSFORM bytes, and post-transform is the whole point: the
    /// case that bites is a small anchored insert into a note already near the
    /// ceiling, where the INPUT is a few KB. Byte length, not string length —
    /// these are already the UTF-8 bytes headed for disk, so a note heavy in
    /// non-ASCII cannot slip past a character count.
    ///
    /// Called from every write path (edit/append via Mutate, create, and each
    /// batch plan) rather than from AtomicFile: batch must reject during its
    /// validate phase, before the first byte lands, or a bad item aborts a
    /// batch halfway. Every call site is pinned by SyncSizeLimitTests — named
    /// in plain text, not a cref: Core cannot reference the test assembly, so
    /// a cref to any test class here is unresolvable by construction.
    /// </summary>
    private void RequireSyncable(VaultPath vp, byte[] after)
    {
        if (after.LongLength <= syncOptions.MaxFileBytes)
            return;
        throw new KnapperException(VaultErrorCode.TooLargeToSync,
            $"{vp.Relative} would be {after.LongLength} bytes; Obsidian Sync carries at most " +
            $"{syncOptions.MaxFileBytes} (Sync__MaxFileBytes). The write is refused: it would " +
            "verify on disk and commit to git while never reaching any device. Split the note, " +
            "or raise the limit if your Sync plan actually allows more.");
    }

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

    /// <summary>
    /// The same seam for the two later windows, handed
    /// (sourceAbsolute, destinationAbsolute): immediately before the source
    /// pathname is CAPTURED — the last instant an external writer can replace
    /// it, and the window whose old check-then-delete handling deleted their
    /// write and reported success — and immediately after the destination is
    /// committed, before it is verified. Never set outside tests.
    /// </summary>
    internal Action<string, string>? BeforeCommitTestHook;
    internal Action<string, string>? BeforeCaptureTestHook;
    internal Action<string, string>? AfterCaptureTestHook;
    internal Action<string, string>? AfterCommitTestHook;

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
            AssertGates([vp]);
            var written = Encoding.UTF8.GetBytes(text);
            RequireSyncable(vp, written);
            using (locks.AcquirePathLock(vp, LockTimeout))
            {
                AssertGates([vp]); // again with the lock held — see AssertGates
                RequireAuditIntent("create", vp.Relative, ctx);
                // Containment on both sides of the commit — see Mutate.
                RequireIntendedTargetInsideVault(vp.Absolute, "create");
                AtomicFile.CreateNew(vp.Absolute, written);
                AtomicFile.VerifyOnDisk(vp.Absolute, written);
                RequireLinkInsideVault(vp.Absolute, "create", "target");
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
            // The full gate pair, not syncGate alone: mkdir is a mutation and
            // the conflict gate governs mutations. Asserted twice around the
            // lock for the reason AssertGates documents.
            AssertGates([vp]);
            using (locks.AcquirePathLock(vp, LockTimeout))
            {
                AssertGates([vp]);
                // Under the lock, so the AlreadyExists answer is still true
                // when it is acted on — outside it this was check-then-act
                // and two concurrent mkdirs of one path both reported success.
                if (Directory.Exists(vp.Absolute) || File.Exists(vp.Absolute))
                    throw new KnapperException(VaultErrorCode.AlreadyExists, $"already exists: {vp.Relative}");
                var parent = Path.GetDirectoryName(vp.Absolute)!;
                if (!Directory.Exists(parent))
                {
                    throw new KnapperException(VaultErrorCode.NotFound,
                        $"parent directory does not exist: create it first (one deliberate level at a time)");
                }
                RequireAuditIntent("mkdir", vp.Relative, ctx);
                // Containment on both sides of the commit, like every other
                // mutation. Resolve rejects symlinked components, but that
                // answer expires one syscall later, and CreateDirectory
                // FOLLOWS a parent swapped for an out-of-vault symlink — the
                // directory is then created outside the vault and the receipt
                // says otherwise. The post check turns that into a typed
                // PathOutsideVault. It removes nothing: unlinking a path on
                // the strength of having created it a syscall earlier is the
                // shape this layer refuses everywhere.
                RequireIntendedTargetInsideVault(vp.Absolute, "mkdir");
                Directory.CreateDirectory(vp.Absolute);
                RequireLinkInsideVault(vp.Absolute, "mkdir", "target");
            }
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
            // SOURCE only. Moving a note INTO an archived prefix is the act of
            // archiving it; moving one OUT changes what the archive holds.
            RequireNotArchived(source, "move");
            AssertGates([source, destination]);

            using (locks.AcquirePathLocks([source, destination], LockTimeout))
            {
                // Again, with the locks HELD: the pre-lock pass above is a
                // fast rejection, and up to Vault:LockTimeoutMs can pass
                // between the two.
                AssertGates([source, destination]);
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

                // Hard links throughout: same inode → no data copy, mode and
                // mtime ride along, content cannot diverge mid-move. The
                // destination is COMMITTED with link(2) because rename(2)
                // would silently replace a destination that appeared since
                // the check above; the source pathname is CAPTURED with
                // rename(2) because unlink(2) would destroy whatever is
                // there now rather than what was checked. See
                // LinkPublishCapture.
                RequireAuditIntent("move", source.Relative, ctx);
                LinkPublishCapture(source, destination.Absolute, data, "move");

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
            RequireNotArchived(vp, "delete");
            AssertGates([vp]);

            using (locks.AcquirePathLock(vp, LockTimeout))
            {
                AssertGates([vp]); // again with the lock held — see Move
                var data = ReadExisting(vp);
                RequireSha(expectSha256, data, vp);
                // Intent BEFORE the first filesystem side effect — the trash
                // directory mkdir below is already a change no audit line
                // would explain if the sink were down.
                RequireAuditIntent("delete", vp.Relative, ctx);

                // Trash paths are built from the ALREADY-VALIDATED relative
                // path — .trash/ is deliberately unreachable via the resolver.
                var trashRelative = ".trash/" + vp.Relative;
                var trashAbsolute = Path.Combine(resolver.Root, trashRelative);
                // ...which means the ONE gate that ran over this path never
                // saw this half of it. `.trash` and every directory under it
                // is a chain nothing has checked, and a symlink anywhere in
                // it sends link(2) — and with it the only remaining copy of a
                // note being deleted — outside the vault, out of git, out of
                // every backup assumption, while the receipt still names a
                // `.trash/...` path. Checked twice on purpose: the second
                // pass is what CreateDirectory (which follows an existing
                // directory symlink) could otherwise walk straight through.
                var trashChain = TrashChainSegments(vp.Relative);
                RequireNoSymlinkedTrashChain(trashChain, trashRelative);
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
                RequireNoSymlinkedTrashChain(trashChain, trashRelative);

                // Same shape as move — the destination is a .trash pathname
                // instead of a vault one.
                LinkPublishCapture(vp, trashAbsolute, data, "delete");

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
        BatchWideStage(resolved, ctx, () => AssertGates(resolved));

        using (BatchWideStage(resolved, ctx, () => locks.AcquirePathLocks(resolved, LockTimeout))) // rejects duplicate paths
        {
            // Validate phase: plan every write. Any failure here = nothing mutated.
            var plans = new List<(VaultPath Vp, byte[]? Before, byte[] After, BatchItem Item)>();
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var vp = resolved[i];
                try
                {
                    // In VALIDATE, like every other refusal here: one
                    // archived item fails the whole batch untouched rather
                    // than aborting it halfway. Create is exempt for the same
                    // reason it is exempt on the single-item surface — a batch
                    // is how a trim-and-archive is done in one round trip.
                    if (item.Kind is not BatchItemKind.Create)
                        RequireNotArchived(vp, "batch " + item.Kind.ToString().ToLowerInvariant());

                    var plan = item.Kind switch
                    {
                        BatchItemKind.Edit => PlanEdit(vp, item),
                        BatchItemKind.Append => PlanAppend(vp, item),
                        BatchItemKind.Create => PlanCreate(vp, item),
                        _ => throw new KnapperException(VaultErrorCode.InvalidArgument, $"unknown batch kind {item.Kind}"),
                    };
                    // In the VALIDATE phase, so an oversized item fails the
                    // whole batch untouched rather than aborting it halfway.
                    RequireSyncable(plan.Item1, plan.Item3);
                    plans.Add(plan);
                }
                catch (Exception e) when (e is KnapperException or IOException or UnauthorizedAccessException)
                {
                    var ke = NormalizeIo(e, "batch-validate", vp.Relative);
                    TryAudit("batch-validate", vp.Relative, ke.Code.ToString(), ctx);
                    throw new KnapperException(ke.Code,
                        $"batch item {i} ({vp.Relative}) failed validation — nothing was mutated: {ke.Message}", ke);
                }
            }

            // Gates again, with every lock held and validation done — the
            // last point at which nothing has been written yet, and the one
            // that matters for a batch: validating N items can take
            // meaningfully longer than acquiring the locks did.
            BatchWideStage(resolved, ctx, () => AssertGates(resolved));

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
                    // Containment on both sides of the commit — see Mutate.
                    RequireIntendedTargetInsideVault(vp.Absolute, opName);
                    if (item.Kind == BatchItemKind.Create)
                        AtomicFile.CreateNew(vp.Absolute, after);
                    else
                        AtomicFile.Replace(vp.Absolute, after, VaultHash.Sha256Hex(before!));
                    AtomicFile.VerifyOnDisk(vp.Absolute, after);
                    RequireLinkInsideVault(vp.Absolute, opName, "target");
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
                    TryAudit(opName, vp.Relative, ke.Code.ToString(), ctx, detail: NoteRecoveredSibling(ke));
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
            RequireNotArchived(vp, op);
            AssertGates([vp]);

            using (locks.AcquirePathLock(vp, LockTimeout))
            {
                AssertGates([vp]); // again with the lock held — see AssertGates
                var (before, after) = transform(vp);
                RequireSyncable(vp, after);
                var beforeSha = VaultHash.Sha256Hex(before);
                RequireAuditIntent(op, vp.Relative, ctx);
                // Containment on BOTH sides of the commit (same rule as
                // move/delete): the resolver's symlink walk happened before
                // the lock wait, so re-prove before writing — and again
                // after, because the pre-proof describes the directory as it
                // was one syscall ago. The post-proof is what stands between
                // a parent swapped mid-write and a success receipt for bytes
                // that landed outside the vault.
                RequireIntendedTargetInsideVault(vp.Absolute, op);
                AtomicFile.Replace(vp.Absolute, after, beforeSha);
                AtomicFile.VerifyOnDisk(vp.Absolute, after);
                RequireLinkInsideVault(vp.Absolute, op, "target");
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
            TryAudit(op, vp.Relative, ke.Code.ToString(), ctx, detail: NoteRecoveredSibling(ke));
            if (ReferenceEquals(ke, e))
                throw;
            throw ke;
        }
    }

    /// <summary>
    /// A raced replace that had to republish a displaced external version
    /// says so in the exception's Data (<see cref="AtomicFile.RecoveredPathDataKey"/>).
    /// The vault visibly changed even though the mutation failed, so the
    /// generation moves, and the audit entry carries the sibling's name —
    /// the durable reconciliation pointer that survives a lost MCP response.
    /// Filename only: path-derived, never note content.
    /// </summary>
    private string? NoteRecoveredSibling(KnapperException ke)
    {
        if (!ke.Data.Contains(AtomicFile.RecoveredPathDataKey)
            || ke.Data[AtomicFile.RecoveredPathDataKey] is not string recovered)
        {
            return null;
        }
        generation.Increment();
        return "displaced external version preserved → " + recovered;
    }

    // ---- the shared link/publish/capture core --------------------------

    /// <summary>
    /// Move and soft delete are the same operation with different
    /// destinations, and both run through here so the ordering exists once.
    ///
    /// <para>Two rules govern the order, and they were each learned from a
    /// defect in the version before:</para>
    /// <list type="bullet">
    /// <item><b>Knapper never removes a pathname another writer could
    /// own.</b> The only names it deletes are hidden temps it created under a
    /// fresh GUID. Everything public is either linked (no-clobber — the
    /// kernel refuses rather than replacing) or captured by rename(2) and
    /// examined afterwards. A check-then-<c>unlink</c> cannot be made safe:
    /// every check has expired by the next syscall and POSIX has no
    /// inode-conditional unlink(2), so the version that re-verified the
    /// source and then deleted it destroyed an external writer's replacement
    /// while reporting SUCCESS (`SourceCaptureRaceTests`).</item>
    /// <item><b>A public pathname holds the content at every instant — as
    /// far as this operation's own actions reach.</b> The destination is
    /// published BEFORE the source is captured, so no step of ours — and no
    /// crash between steps — leaves the note only under hidden names. The
    /// version that captured first put an fsynced rename between the
    /// source's disappearance and the destination's creation: a process
    /// death there left the bytes reachable through nothing an agent, a
    /// query, a health walk or git could see, while Sync propagated the
    /// deletion (`CrashDurabilityTests`).</item>
    /// </list>
    ///
    /// <para><b>The commit boundary</b>: once the destination is published
    /// and VERIFIED, the operation is committed. An external writer that
    /// removes or replaces that destination afterwards — even before the
    /// source capture and cleanup finish — has deleted or overwritten the
    /// note, exactly as if it had acted a millisecond after the operation
    /// returned, and the operation still reports success. That is the
    /// linearizable reading and it is deliberate: the alternative, holding a
    /// hidden recovery link and deciding later whether the destination "still"
    /// holds ours, is check-then-act over a pathname other writers own, the
    /// exact shape this design removed. `DestinationRaceTests` pins both
    /// sides of the boundary.</para>
    ///
    /// <para>The price of publishing first is that a source replaced in the
    /// narrow window before the capture leaves the destination published — a
    /// visible duplicate of the old content, named in the error and audited,
    /// rather than a retraction of a pathname other writers can already see.
    /// That trade is deliberate: a duplicate is visible and repairable by a
    /// human; the alternatives are deleting something that may not be ours,
    /// or a note that exists nowhere anything can find it.</para>
    ///
    /// <para>Steps: prove the source resolves inside the vault → link a
    /// private second name and verify it → prove destination containment →
    /// confirm the source is still what was authorized (a courtesy check: it
    /// prevents publishing a destination we would then have to abandon, and
    /// NOTHING destructive rests on it) → commit the destination with
    /// link(2) → prove destination containment again, because a directory
    /// can be swapped between the check and the link → verify the
    /// destination by content → capture the source pathname with rename(2) →
    /// prove the CAPTURED name resolves inside the vault → confirm what we
    /// captured was ours, and link it back if it was not.</para>
    /// </summary>
    private void LinkPublishCapture(VaultPath source, string destinationAbsolute, byte[] data, string operation)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationAbsolute)!;
        var sourceDirectory = Path.GetDirectoryName(source.Absolute)!;
        var temp = Path.Combine(destinationDirectory, AtomicFile.TempPrefix + operation + "-" + Guid.NewGuid().ToString("N"));
        string? captured = null;
        var published = false;
        var keepCaptured = false;
        var keepTemp = false;

        // The SOURCE gets the same physical-containment treatment as the
        // destination: the resolver's symlink walk ran before the lock wait,
        // and a parent swapped since then would make the link below copy an
        // OUT-OF-VAULT file's content into the vault — and the capture later
        // remove that outside file's pathname — while the receipt names a
        // vault note. Narrow here, and prove again on the captured name
        // below, where it is load-bearing.
        RequireLinkInsideVault(source.Absolute, operation, "source");

        // 1. A private second name for the content. NO-FOLLOW, deliberately:
        //    plain link(2) diverges on a source that has just been swapped
        //    for a symlink — macOS follows it (hard-linking the OUT-OF-VAULT
        //    target into the vault; reproduced 2026-08-20), Linux links the
        //    symlink inode (publishing a symlink into the vault). linkat(…,0)
        //    captures whatever the final component IS under our private
        //    name, where the next line can inspect it without following.
        BeforeLinkTestHook?.Invoke(source.Absolute);
        Posix.LinkNoFollow(source.Absolute, temp);
        try
        {
            AfterLinkTestHook?.Invoke(source.Absolute);
            Posix.FsyncDirectory(destinationDirectory);
            var linked = Posix.LStat(temp);
            if (linked.IsSymlink)
            {
                throw new KnapperException(VaultErrorCode.SymlinkRejected,
                    $"{source.Relative} was replaced by a symlink after it was checked — the {operation} " +
                    "is refused; nothing was published and nothing was removed (symlinks are rejected " +
                    "everywhere, always)");
            }
            if (!linked.IsRegular)
            {
                // A FIFO here would BLOCK the verify read below forever while
                // this operation holds its path locks; a socket or device is
                // not vault content at all. Classified before any read.
                throw new KnapperException(VaultErrorCode.PreconditionFailed,
                    $"{source.Relative} was replaced by a non-regular file (FIFO, socket, or device) after " +
                    $"it was checked — the {operation} is refused; nothing was published and nothing was removed");
            }
            try
            {
                AtomicFile.VerifyOnDisk(temp, data);
            }
            catch (KnapperException e) when (e.Code == VaultErrorCode.VerifyFailed)
            {
                throw new KnapperException(VaultErrorCode.PreconditionFailed,
                    $"{source.Relative} changed between read and link (external writer) — " +
                    $"{operation} rolled back; re-read and retry", e);
            }
            RequireLinkInsideVault(temp, operation);

            // A courtesy check, NOT a safety mechanism: publishing a
            // destination for content the source no longer holds would leave
            // a duplicate we are not allowed to retract. Catching the common
            // case here keeps that duplicate rare. Safety comes from the
            // capture below, which is atomic and destroys nothing.
            RequireStillOurBytes(source, data,
                $"changed while the {operation} was being prepared — nothing was published or removed");

            // 2. Publish the destination — the first vault-visible change.
            //    link(2), so a destination that appeared since the existence
            //    check is refused, never replaced.
            BeforeCommitTestHook?.Invoke(source.Absolute, destinationAbsolute);
            Posix.Link(temp, destinationAbsolute);
            published = true;
            Posix.FsyncDirectory(destinationDirectory);
            AfterCommitTestHook?.Invoke(source.Absolute, destinationAbsolute);

            // Containment AGAIN, on the published name: the pre-commit proof
            // covers the directory as it was one syscall ago, and a directory
            // can be moved out of the vault and replaced by a symlink in
            // between. Here the source is still untouched, so detecting it is
            // a clean failure (`TrashChainTests`).
            RequireLinkInsideVault(destinationAbsolute, operation);
            try
            {
                AtomicFile.VerifyOnDisk(destinationAbsolute, data);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Deliberately every exception, not IOException alone:
                // File.ReadAllBytes answers UnauthorizedAccessException when
                // the destination has become a directory or an unreadable
                // file, and that escaping the handler is how the version
                // before lost the original note outright while reporting a
                // typed IoError (`PostCommitFailureTests`). Nothing here is
                // destructive — the source is still at its own pathname —
                // so the broad catch costs nothing and closes the class.
                throw new KnapperException(VaultErrorCode.PreconditionFailed,
                    $"the {operation} destination did not survive being written: an external writer replaced " +
                    $"or removed '{Path.GetFileName(destinationAbsolute)}' — their file was left untouched, " +
                    $"nothing was removed, and '{source.Relative}' is still at its own path. " +
                    $"Cause: {e.Message}", e);
            }

            // 3. Capture the source pathname — rename, never unlink.
            BeforeCaptureTestHook?.Invoke(source.Absolute, destinationAbsolute);
            var captureTarget = Path.Combine(sourceDirectory,
                AtomicFile.TempPrefix + operation + "-captured-" + Guid.NewGuid().ToString("N"));
            // `captured` is set only once the rename has actually happened —
            // a failed capture (the source vanished) holds nothing, and
            // treating it as if it did would send the rollback looking for a
            // file that never existed and report the wrong failure.
            Posix.Rename(source.Absolute, captureTarget);
            captured = captureTarget;
            Posix.FsyncDirectory(sourceDirectory);
            AfterCaptureTestHook?.Invoke(source.Absolute, destinationAbsolute);

            // Containment AGAIN, on the captured name: if the source's parent
            // was swapped after the pre-check, the rename above captured a
            // file OUTSIDE the vault, and the success path would delete that
            // capture — removing an out-of-vault file the receipt never
            // mentions. Detected here, the rollback links it straight back
            // where it was taken from; nothing is destroyed. (A directory
            // moved out AFTER the capture makes this throw spuriously — the
            // rollback then restores or keeps, both in the KEEP direction.)
            RequireLinkInsideVault(captured, operation, "source");

            // Was what we captured the file we were authorized to move? If
            // not, an external writer replaced the source after the courtesy
            // check, and their write is now under our private name — the
            // rollback below is the ONE place that links it back.
            if (!CapturedIsOurs(captured, data))
            {
                throw new KnapperException(VaultErrorCode.PreconditionFailed,
                    $"{source.Relative} was replaced by an external writer (Sync or a human) during the " +
                    $"{operation} — their write is left in place. NOTE: the destination had already been " +
                    $"published and is NOT retracted (retracting a pathname other writers can see is how " +
                    $"their data gets destroyed), so a copy of the previous content remains at " +
                    $"'{Path.GetFileName(destinationAbsolute)}' for a human to remove. Re-read and retry.");
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Rollback is two questions, both answered in the KEEP direction
            // when they cannot be answered confidently.
            //
            // First: if the source pathname was captured, put it back.
            // link(2), so a name another writer has taken since is refused
            // rather than replaced. Nothing public is ever removed here — not
            // the destination, not even a link that escaped the vault.
            if (captured is not null && !TryRestoreSource(captured, source.Absolute, sourceDirectory))
                keepCaptured = true;

            // Second: cleanup must never drop the LAST link to the content.
            // Publishing first guarantees a public pathname exists at every
            // instant of the happy path, but an external writer can replace
            // BOTH pathnames while this operation is failing — and then the
            // only remaining link is a hidden temp this method is about to
            // delete. There is no portable link count to consult (st_nlink
            // means stat(2)), so the test is the honest one: does any public
            // pathname still hold the authorized bytes? If not, the temp
            // stays and the error says where it is. Keeping residue on
            // uncertainty is always available; deleting the last copy is not
            // recoverable.
            // ...and only when THIS operation is what took the content out of
            // public view. If an external writer replaced the pathnames
            // themselves, the previous version is theirs to have replaced —
            // hoarding a hidden copy of every note Sync overwrites mid-race
            // would fill the vault with invisible files nobody asked for. The
            // capture is the only removal Knapper performs, so it is the only
            // thing that can put us in that position.
            keepTemp = captured is not null
                && HiddenLinkIsTheLastCopy(temp, source.Absolute, destinationAbsolute);
            if (keepTemp || keepCaptured)
            {
                var kept = string.Join("' and '", new[]
                    {
                        keepTemp ? Path.GetFileName(temp) : null,
                        keepCaptured ? Path.GetFileName(captured!) : null,
                    }.Where(n => n is not null));
                throw new KnapperException(VaultErrorCode.VerifyFailed,
                    $"{source.Relative}: the {operation} failed and content could not be left under a normal " +
                    $"pathname — an external writer took the names involved. Nothing is lost: it is linked at " +
                    $"'{kept}' (hidden files, so invisible to queries and not committed). A human must place " +
                    $"them. Cause: {e.Message}", e);
            }
            throw;
        }
        finally
        {
            if (!keepTemp)
                TryDeleteTemp(temp);
            if (captured is not null && !keepCaptured)
                TryDeleteTemp(captured);
            Posix.FsyncDirectory(destinationDirectory);
            Posix.FsyncDirectory(sourceDirectory);
            _ = published; // the flag documents the ordering; no path retracts
        }
    }

    /// <summary>
    /// True when the captured pathname holds the bytes the operation was
    /// authorized against. Content, not identity — but nothing destructive
    /// hangs on the answer any more: a false here restores the captured file
    /// to its own pathname, so being wrong costs a spurious failure, never a
    /// deletion.
    /// </summary>
    /// <summary>
    /// Would deleting this hidden link drop the last copy of what it holds?
    ///
    /// <para>Asked only on failure paths, and only ever to decide whether one
    /// of Knapper's OWN temps may be removed — never whether something public
    /// may be. The question is about the temp's CURRENT content, not the
    /// authorized bytes: when an external writer replaced the source before
    /// the link, the temp holds THEIR file, which still sits at its own
    /// pathname, and keeping a hidden duplicate of it would be residue for
    /// nothing. When they replaced both pathnames mid-operation, the temp is
    /// the only link left to the original and must survive.</para>
    ///
    /// <para>Anything unreadable answers "yes, keep it". Being wrong in that
    /// direction costs a hidden file a human deletes; being wrong in the other
    /// direction is not recoverable.</para>
    /// </summary>
    private static bool HiddenLinkIsTheLastCopy(string tempPath, string sourceAbsolute, string destinationAbsolute)
    {
        byte[] held;
        try
        {
            held = File.ReadAllBytes(tempPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
        return !Holds(sourceAbsolute) && !Holds(destinationAbsolute);

        bool Holds(string path)
        {
            try
            {
                // Same non-following discipline as every other judgement: a
                // non-regular file at the public name neither holds our bytes
                // nor may be read (a FIFO read would hang the rollback).
                // Answering false keeps the temp — the safe direction.
                return File.Exists(path)
                    && Posix.LStat(path).IsRegular
                    && File.ReadAllBytes(path).AsSpan().SequenceEqual(held);
            }
            catch (KnapperException)
            {
                return false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// A NON-DESTRUCTIVE precondition: the source still holds what the
    /// operation was authorized against.
    ///
    /// <para>Its ancestor sat immediately before a <c>File.Delete</c> of the
    /// source and was believed to make that delete safe. It did not — the
    /// check expires the instant it returns, and deleting on the strength of
    /// it destroyed an external writer's replacement while reporting success.
    /// Nothing destructive may EVER be gated on this again. Its only job now
    /// is to avoid publishing a destination the operation is about to
    /// abandon, because a published pathname is one this layer refuses to
    /// retract.</para>
    /// </summary>
    private static void RequireStillOurBytes(VaultPath vp, byte[] expected, string whatHappened)
    {
        bool same;
        try
        {
            // Non-following type check first: a source swapped for a FIFO
            // would hang this read under the path locks, and a symlink would
            // compare some other file's bytes. Either way: not our content.
            same = File.Exists(vp.Absolute)
                && Posix.LStat(vp.Absolute).IsRegular
                && File.ReadAllBytes(vp.Absolute).AsSpan().SequenceEqual(expected);
        }
        catch (KnapperException)
        {
            same = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // UnauthorizedAccessException, not IOException, is what
            // File.ReadAllBytes answers for a mode-000 file — the same
            // exhaustive pair the sibling judgements (CapturedIsOurs, Holds)
            // catch. Omitting it let a source chmod'ed unreadable inside this
            // window escape as [IoError] ("filesystem failure") when the
            // honest answer is [PreconditionFailed] ("re-read and retry"):
            // an unreadable source cannot be PROVEN to still hold our bytes,
            // and unprovable is exactly what this precondition rejects.
            same = false;
        }
        if (!same)
        {
            throw new KnapperException(VaultErrorCode.PreconditionFailed,
                $"{vp.Relative} {whatHappened}; an external writer (Sync or a human) raced this " +
                "operation — re-read and retry against current content");
        }
    }

    private static bool CapturedIsOurs(string capturedPath, byte[] data)
    {
        try
        {
            // Never through a symlink, and never a blind read: if an external
            // writer swapped the source after the publish, the capture rename
            // took whatever was there — reading THROUGH a symlink would
            // compare some other file's content and, on a match, let the
            // success path delete a pathname that was never ours; reading a
            // FIFO would block forever under the path locks. Non-regular
            // answers not-ours, which routes to the rollback's no-follow
            // restore.
            if (!Posix.LStat(capturedPath).IsRegular)
                return false;
            return File.ReadAllBytes(capturedPath).AsSpan().SequenceEqual(data);
        }
        catch (KnapperException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Prove a pathname is really inside the vault, by resolving it rather
    /// than by trusting the string.
    ///
    /// <para>Every mutation path is checked for symlinked components before
    /// it acts — the resolver at <c>Resolve</c>,
    /// <c>RequireNoSymlinkedTrashChain</c> for a delete's trash chain — but a
    /// check against a directory chain is only true until it is not: a
    /// component swapped for a symlink between the check and the syscall is a
    /// window nothing short of descriptor-relative operations can close, and
    /// not even those survive a directory MOVED out of the vault (a handle
    /// follows it). This does not close the window either. What it does is
    /// stop the CONSEQUENCE, which is the part that matters: called before a
    /// destructive step, it refuses while everything still exists under its
    /// own name; called after a commit, it turns a success receipt for an
    /// out-of-vault write into a loud failure. The alternative is a note that
    /// left the vault, left git, left every backup, and a receipt that says
    /// otherwise.</para>
    /// </summary>
    /// <summary>
    /// The PRE-write half of the containment proof, tolerant of a parent that
    /// does not exist: a missing directory must surface as the operation's
    /// own typed <see cref="VaultErrorCode.NotFound"/> (create's contract),
    /// not as realpath's IoError. Skipping the proof there gives nothing
    /// away — a chain that does not resolve is one no write can land
    /// through, and the strict post-commit proof runs after every successful
    /// write, where the chain necessarily resolves.
    /// </summary>
    private void RequireIntendedTargetInsideVault(string absolute, string operation)
    {
        if (Directory.Exists(Path.GetDirectoryName(absolute)!))
            RequireLinkInsideVault(absolute, operation, "target");
    }

    private void RequireLinkInsideVault(string linkAbsolute, string operation, string role = "destination")
    {
        var directory = Posix.RealPath(Path.GetDirectoryName(linkAbsolute)!);
        if (directory != resolver.Root
            && !directory.StartsWith(resolver.Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new KnapperException(VaultErrorCode.PathOutsideVault,
                $"the {operation} {role} resolves to '{directory}', outside the vault — a directory in " +
                "its path was replaced or moved after it was checked. The operation reports failure and " +
                "removes nothing.");
        }
    }

    /// <summary>
    /// Put the original content back at the source pathname after a failed
    /// unlink-side race. link semantics, not rename: if another writer has
    /// already taken the pathname, the kernel refuses rather than replacing
    /// them. NO-FOLLOW, so a captured SYMLINK is restored as the symlink it
    /// was — plain link(2) on macOS would follow it and plant a hard link to
    /// its out-of-vault target at the source name instead.
    /// </summary>
    private static bool TryRestoreSource(string recovery, string sourceAbsolute, string sourceDirectory)
    {
        try
        {
            Posix.LinkNoFollow(recovery, sourceAbsolute);
            Posix.FsyncDirectory(sourceDirectory);
            return true;
        }
        catch (KnapperException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>The <c>.trash</c> directory chain a soft delete will link through, root-relative.</summary>
    private static IReadOnlyList<string> TrashChainSegments(string sourceRelative)
    {
        var segments = new List<string> { ".trash" };
        if (Path.GetDirectoryName(sourceRelative) is { Length: > 0 } parent)
            segments.AddRange(parent.Split('/', StringSplitOptions.RemoveEmptyEntries));
        return segments;
    }

    /// <summary>
    /// The resolver's own symlink rule, applied to the one path family the
    /// resolver never sees. `.trash` stays unaddressable — this does not make
    /// it reachable, it holds Knapper's OWN construction to the same standard
    /// as an agent's.
    /// </summary>
    private void RequireNoSymlinkedTrashChain(IReadOnlyList<string> segments, string trashRelative) =>
        VaultPathResolver.RejectSymlinkComponents(resolver.Root, segments, trashRelative);

    /// <summary>
    /// The conflict and sync gates. Called TWICE per mutation: once before
    /// the locks as a fast rejection, once with them held. Waiting for a lock
    /// can take up to Vault:LockTimeoutMs, and a batch's validate phase adds
    /// more — long enough for Sync to materialize a conflict sibling or for
    /// the heartbeat to cross its maximum age while a mutation sits in the
    /// queue holding an answer from before it got there.
    ///
    /// <para>This NARROWS a window; it cannot close one. The locks bind
    /// cooperating Knapper processes only, so a conflict file can still
    /// appear the instant after any check, held locks or not. What the second
    /// pass buys is that the gate result a write acts on was taken after the
    /// waiting, not before it.</para>
    /// </summary>
    private void AssertGates(IReadOnlyList<VaultPath> paths)
    {
        foreach (var vp in paths)
            conflicts.AssertNotConflicted(vp);
        syncGate.AssertMutationsAllowed();
    }

    /// <summary>
    /// The audited boundary for a BATCH-WIDE rejection: the gates (both
    /// passes), duplicate-path refusal, and lock timeout fail the whole batch
    /// before any write, and each used to do so without an audit entry —
    /// unlike every single-item operation, whose outer catch audits the same
    /// rejections. One entry lands per resolved path (a stale gate answer or
    /// a lock that never came refused every one of them), with a detail
    /// marking it batch-wide; per-item validate/apply failures keep their own
    /// audit paths and never pass through here, so nothing is audited twice.
    /// Paths and counts only — never note content.
    /// </summary>
    private T BatchWideStage<T>(IReadOnlyList<VaultPath> resolved, AuditContext? ctx, Func<T> stage)
    {
        try
        {
            return stage();
        }
        catch (Exception e) when (e is KnapperException or IOException or UnauthorizedAccessException)
        {
            var ke = NormalizeIo(e, "batch", $"{resolved.Count} paths");
            foreach (var vp in resolved)
            {
                TryAudit("batch", vp.Relative, ke.Code.ToString(), ctx,
                    detail: $"batch of {resolved.Count} rejected before any write");
            }
            if (ReferenceEquals(ke, e))
                throw;
            throw ke;
        }
    }

    private void BatchWideStage(IReadOnlyList<VaultPath> resolved, AuditContext? ctx, Action stage) =>
        _ = BatchWideStage<object?>(resolved, ctx, () =>
        {
            stage();
            return null;
        });

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

    private static void TryDeleteTemp(string absolutePath)
    {
        try
        {
            File.Delete(absolutePath);
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
        // Classify before reading: this is the FIRST read of every mutation,
        // and it runs holding the path locks — a FIFO here would hang the
        // whole operation (open(2) for read blocks until a writer appears),
        // and a symlink swapped in since Resolve would read some other
        // file's bytes as the note's.
        if (!Posix.LStat(vp.Absolute).IsRegular)
        {
            throw new KnapperException(VaultErrorCode.PreconditionFailed,
                $"{vp.Relative} is not a regular file (a symlink, FIFO, socket, or device was put at its " +
                "pathname by an external writer) — refused");
        }
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
            // Explicit null checks: Core owes its own typed [InvalidArgument]
            // for every malformed shape — a null slipping through binds as a
            // NullReferenceException and reaches agents as [Internal], which
            // they treat as a server bug, not a fixable request.
            if (edits[i] is null)
                throw new KnapperException(VaultErrorCode.InvalidArgument, $"edit[{i}] is null");
            if (string.IsNullOrEmpty(edits[i].Old))
                throw new KnapperException(VaultErrorCode.InvalidArgument, $"edit[{i}]: 'old' must be non-empty");
            if (edits[i].New is null)
                throw new KnapperException(VaultErrorCode.InvalidArgument,
                    $"edit[{i}]: 'new' is required (use \"\" to delete the anchored text)");
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
