using System.Text;
using Knapper.Core.Mutation;
using Knapper.Core.Vault;

namespace Knapper.Core.Tests.Mutation;

/// <summary>
/// The final-check/commit window of <see cref="AtomicFile.Replace"/> — the
/// review's P0, in its second round. The first remediation (atomic exchange)
/// stopped the physical destruction of a raced external write but left the
/// STALE AGENT EDIT canonical with the external bytes hidden — matching
/// neither serialization of the two writes and violating brief §7's "reject
/// stale input without mutating" (reviewer follow-up, 2026-08-20). The
/// contract now: a raced commit is exchanged BACK — the external bytes
/// return to the canonical pathname and the rejection is clean. Only when a
/// third write or delete lands in the microseconds between the two exchanges
/// does the fallback engage: every surviving version stays VISIBLE (a
/// `(Knapper displaced …)` conflict sibling, blocking the note until a human
/// reconciles) — never hidden-only, and never our stale bytes at the note's
/// pathname.
///
/// <para>Every test asserts the CONTENTS of the canonical pathname and the
/// whereabouts of every surviving version, not just the error code.</para>
///
/// <para>The hooks are static (AtomicFile is) and xunit runs test classes in
/// parallel, so every class that ASSIGNS them shares one collection — a
/// parallel class would silently overwrite another's hook — and every hook
/// body still filters on its own vault's path.</para>
/// </summary>
[Collection("AtomicFile static test hooks")]
public sealed class ReplaceCommitRaceTests : IDisposable
{
    private readonly MutationVault _v = new();

    public void Dispose()
    {
        AtomicFile.BeforeExchangeTestHook = null;
        AtomicFile.AfterRacedExchangeTestHook = null;
        _v.Dispose();
    }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>Replace like Sync does: temp sibling, rename over — a NEW inode.</summary>
    private static void ExternalReplace(string absolutePath, string newContent)
    {
        var temp = absolutePath + ".sync-replace";
        File.WriteAllText(temp, newContent);
        File.Move(temp, absolutePath, overwrite: true);
    }

    private void OnExchangeOf(string relative, Action<string> action)
    {
        // The resolver's canonical absolute — the string the service hands
        // AtomicFile — not TempDir's spelling (macOS: /var vs /private/var).
        var target = _v.Resolver.Resolve(relative).Absolute;
        AtomicFile.BeforeExchangeTestHook = path =>
        {
            if (path == target)
                action(path);
        };
    }

    private void OnRacedExchangeOf(string relative, Action<string> action)
    {
        var target = _v.Resolver.Resolve(relative).Absolute;
        AtomicFile.AfterRacedExchangeTestHook = path =>
        {
            if (path == target)
                action(path);
        };
    }

    /// <summary>
    /// The core conditional-write property: Sync's write linearizes first,
    /// so the agent's stale edit rejects WITHOUT MUTATING — the canonical
    /// pathname holds the external bytes after the rejection, and the
    /// agent's bytes survive nowhere. (Round one left the agent bytes
    /// canonical and hid the external write where nothing syncs or sees it.)
    /// </summary>
    [Fact]
    public void An_external_replacement_in_the_final_window_is_rolled_back_and_stays_canonical()
    {
        _v.Write("note.md", "agent base\n");
        var path = _v.Resolver.Resolve("note.md").Absolute;
        OnExchangeOf("note.md", p => ExternalReplace(p, "sync won\n"));

        var ex = Should.Throw<KnapperException>(() =>
            AtomicFile.Replace(path, Bytes("agent update\n"), VaultHash.Sha256Hex(Bytes("agent base\n"))));

        ex.Code.ShouldBe(VaultErrorCode.PreconditionFailed);
        File.ReadAllText(path).ShouldBe("sync won\n",
            "reject stale input WITHOUT mutating — their write is what a re-read must see");
        _v.TempFiles().ShouldBeEmpty("our discarded bytes are not residue");
    }

    /// <summary>
    /// Overwrite in place — SAME inode, same length — then restore the
    /// mtime, the way mtime-faithful sync tooling does routinely (utimensat
    /// is not an adversary move; timestamp granularity produces the same
    /// aliasing unaided). Leaves dev, inode, size, AND mtime identical to
    /// any earlier stat.
    /// </summary>
    private static void ExternalInPlaceOverwrite(
        string absolutePath, string sameLengthContent, DateTime restoredMtimeUtc)
    {
        File.WriteAllText(absolutePath, sameLengthContent);
        File.SetLastWriteTimeUtc(absolutePath, restoredMtimeUtc);
    }

    private void SiblingsOf(string directoryRelative = "")
        => Directory.EnumerateFiles(Path.Combine(_v.VaultDir.Path, directoryRelative))
            .Select(Path.GetFileName)
            .Where(n => n!.Contains(" (Knapper displaced", StringComparison.Ordinal))
            .ShouldBeEmpty("a clean rejection restores by swap-back and publishes no sibling");

    /// <summary>
    /// Round four's P0: metadata is not a content precondition. The external
    /// write lands on the SAME inode with different equal-length bytes and a
    /// restored mtime, so the displaced object's dev/inode/size/mtime tuple
    /// exactly matches the pre-exchange stat — and a metadata fast path in
    /// the authorized-base judgement accepted it without hashing, deleted
    /// the only copy of the external write with the temp, and reported
    /// success. The judgement is by BYTES now; a matching stamp must change
    /// nothing.
    /// </summary>
    [Fact]
    public void An_in_place_overwrite_with_a_restored_mtime_is_still_a_raced_commit()
    {
        _v.Write("note.md", "agent base\n");
        var path = _v.Resolver.Resolve("note.md").Absolute;
        var fixedMtime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, fixedMtime);
        OnExchangeOf("note.md", p => ExternalInPlaceOverwrite(p, "sync won 1\n", fixedMtime));

        var ex = Should.Throw<KnapperException>(() =>
            AtomicFile.Replace(path, Bytes("agent update\n"), VaultHash.Sha256Hex(Bytes("agent base\n"))));

        ex.Code.ShouldBe(VaultErrorCode.PreconditionFailed);
        File.ReadAllText(path).ShouldBe("sync won 1\n",
            "the external in-place write must be restored canonically — never deleted, never displaced by our stale bytes");
        _v.TempFiles().ShouldBeEmpty("no version of the external write may end up hidden or destroyed");
        SiblingsOf();
    }

    /// <summary>
    /// The replacement variant of round four's P0: a NEW inode carrying
    /// different equal-length bytes and a restored mtime — no reliance on
    /// inode reuse. Identity already fails this stat compare, so this
    /// rejected correctly even while the fast path existed; it is pinned so
    /// the identity half is never promoted back into sufficiency — neither
    /// half of a stat tuple is a content proof.
    /// </summary>
    [Fact]
    public void A_replacement_with_a_restored_size_and_mtime_is_still_a_raced_commit()
    {
        _v.Write("note.md", "agent base\n");
        var path = _v.Resolver.Resolve("note.md").Absolute;
        var fixedMtime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, fixedMtime);
        OnExchangeOf("note.md", p =>
        {
            ExternalReplace(p, "sync won 2\n"); // same length as the base
            File.SetLastWriteTimeUtc(p, fixedMtime);
        });

        var ex = Should.Throw<KnapperException>(() =>
            AtomicFile.Replace(path, Bytes("agent update\n"), VaultHash.Sha256Hex(Bytes("agent base\n"))));

        ex.Code.ShouldBe(VaultErrorCode.PreconditionFailed);
        File.ReadAllText(path).ShouldBe("sync won 2\n");
        _v.TempFiles().ShouldBeEmpty();
        SiblingsOf();
    }

    /// <summary>
    /// The deliberate price of judging by bytes alone: a displaced base made
    /// UNREADABLE mid-window (an external chmod bumps only ctime, so the old
    /// stamp fast path called it untouched and succeeded) can no longer be
    /// PROVEN to be the base, and "cannot prove" routes to the swap-back —
    /// a spurious but SAFE PreconditionFailed over a net-unchanged file,
    /// never a success the hash could not verify.
    /// </summary>
    [Fact]
    public void An_unreadable_displaced_base_rejects_safely_instead_of_succeeding()
    {
        if (Environment.IsPrivilegedProcess)
            return; // root reads through mode 000 — the scenario cannot exist

        _v.Write("note.md", "agent base\n");
        var path = _v.Resolver.Resolve("note.md").Absolute;
        OnExchangeOf("note.md", p => File.SetUnixFileMode(p, UnixFileMode.None));

        var ex = Should.Throw<KnapperException>(() =>
            AtomicFile.Replace(path, Bytes("agent update\n"), VaultHash.Sha256Hex(Bytes("agent base\n"))));

        ex.Code.ShouldBe(VaultErrorCode.PreconditionFailed);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.ReadAllText(path).ShouldBe("agent base\n", "the swap-back leaves the file unchanged net");
        _v.TempFiles().ShouldBeEmpty();
    }

    [Fact]
    public void An_external_delete_in_the_final_window_fails_cleanly_and_resurrects_nothing()
    {
        _v.Write("note.md", "agent base\n");
        var path = _v.Resolver.Resolve("note.md").Absolute;
        OnExchangeOf("note.md", File.Delete);

        var ex = Should.Throw<KnapperException>(() =>
            AtomicFile.Replace(path, Bytes("agent update\n"), VaultHash.Sha256Hex(Bytes("agent base\n"))));

        // The old rename would have recreated the file the external writer
        // just deleted — their delete silently undone. The exchange refuses.
        ex.Code.ShouldBe(VaultErrorCode.PreconditionFailed);
        File.Exists(path).ShouldBeFalse("their delete stands");
        _v.TempFiles().ShouldBeEmpty();
    }

    /// <summary>
    /// A delete landing between the two exchanges — the external writer
    /// removes the just-landed (and never-acknowledged) agent bytes before
    /// the swap-back can run. The displaced external version is restored to
    /// the canonical name by no-clobber link.
    /// </summary>
    [Fact]
    public void A_delete_between_the_exchanges_still_ends_with_the_external_bytes_canonical()
    {
        _v.Write("note.md", "agent base\n");
        var path = _v.Resolver.Resolve("note.md").Absolute;
        OnExchangeOf("note.md", p => ExternalReplace(p, "sync won\n"));
        OnRacedExchangeOf("note.md", File.Delete);

        var ex = Should.Throw<KnapperException>(() =>
            AtomicFile.Replace(path, Bytes("agent update\n"), VaultHash.Sha256Hex(Bytes("agent base\n"))));

        ex.Code.ShouldBe(VaultErrorCode.PreconditionFailed);
        File.ReadAllText(path).ShouldBe("sync won\n", "the displaced external version is restored");
        _v.TempFiles().ShouldBeEmpty();
    }

    [Fact]
    public void An_edit_raced_by_an_external_replacement_rejects_without_mutating()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        OnExchangeOf("Notes/a.md", p => ExternalReplace(p, "sync won\n"));

        var ex = Should.Throw<KnapperException>(() =>
            _v.Service.Edit("Notes/a.md", sha, [new EditSpec("agent base", "agent update")]));

        ex.Code.ShouldBe(VaultErrorCode.PreconditionFailed);
        _v.ReadText("Notes/a.md").ShouldBe("sync won\n",
            "re-read after PreconditionFailed must see the current canonical file");
        _v.TempFiles().ShouldBeEmpty();
        _v.AuditLines().ShouldContain(l => l.Contains("\"PreconditionFailed\""),
            "the raced commit is a rejection, and rejections are audited");
    }

    /// <summary>
    /// A failed batch item must not contain its planned bytes at the
    /// canonical path (the round-one test pinned exactly that defect as
    /// desired behavior — inverted here).
    /// </summary>
    [Fact]
    public void A_batch_item_raced_by_an_external_replacement_fails_without_mutating_it()
    {
        var shaA = _v.Write("a.md", "alpha\n");
        var shaB = _v.Write("b.md", "beta\n");
        OnExchangeOf("b.md", p => ExternalReplace(p, "sync won\n"));

        var result = _v.Service.Batch(
        [
            new BatchItem(BatchItemKind.Edit, "a.md", shaA, [new EditSpec("alpha", "ALPHA")]),
            new BatchItem(BatchItemKind.Edit, "b.md", shaB, [new EditSpec("beta", "BETA")]),
        ]);

        result.AllApplied.ShouldBeFalse();
        result.Items[0].Status.ShouldBe(BatchItemStatus.Applied);
        result.Items[1].Status.ShouldBe(BatchItemStatus.Failed);
        result.Items[1].ErrorCode.ShouldBe(VaultErrorCode.PreconditionFailed);
        _v.ReadText("a.md").ShouldBe("ALPHA\n");
        _v.ReadText("b.md").ShouldBe("sync won\n", "a Failed item's planned bytes must not be canonical");
        _v.TempFiles().ShouldBeEmpty();
    }

    /// <summary>
    /// The double race: a SECOND external write lands in the microseconds
    /// between the two exchanges, so the swap-back reclaims that newer
    /// version instead of our own bytes. Exact rollback is impossible —
    /// so every surviving version must be VISIBLE: the earlier external
    /// version at the canonical name, the newer one as a `(Knapper
    /// displaced …)` conflict sibling that Sync carries, the audit trail
    /// points at, the generation counter reflects, and the conflict gate
    /// blocks on until a human reconciles. Hidden-only survival was the
    /// follow-up review's core objection.
    /// </summary>
    [Fact]
    public void A_third_write_between_the_exchanges_is_preserved_visibly_and_blocks_the_note()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        var generationBefore = _v.Generation.Current;
        OnExchangeOf("Notes/a.md", p => ExternalReplace(p, "sync won\n"));
        OnRacedExchangeOf("Notes/a.md", p => ExternalReplace(p, "sync won again\n"));

        var ex = Should.Throw<KnapperException>(() =>
            _v.Service.Edit("Notes/a.md", sha, [new EditSpec("agent base", "agent update")]));

        ex.Code.ShouldBe(VaultErrorCode.PreconditionFailed);
        ex.Message.ShouldContain("Knapper displaced");
        _v.ReadText("Notes/a.md").ShouldBe("sync won\n", "the earlier external version is restored");

        var sibling = Directory.EnumerateFiles(Path.Combine(_v.VaultDir.Path, "Notes"))
            .Select(Path.GetFileName)
            .Where(n => n!.Contains(" (Knapper displaced", StringComparison.Ordinal))
            .ToList()
            .ShouldHaveSingleItem("the newer external version must survive VISIBLY");
        _v.ReadText("Notes/" + sibling).ShouldBe("sync won again\n");
        _v.TempFiles().ShouldBeEmpty("nothing may survive hidden-only");

        _v.Generation.Current.ShouldBeGreaterThan(generationBefore,
            "the vault visibly changed even though the mutation failed");
        _v.AuditLines().ShouldContain(l => l.Contains("displaced external version preserved"),
            "the audit trail is the durable reconciliation pointer");

        // The conflict gate holds the note until a human reconciles.
        var currentSha = VaultHash.Sha256Hex(Bytes("sync won\n"));
        Should.Throw<KnapperException>(() =>
            _v.Service.Edit("Notes/a.md", currentSha, [new EditSpec("sync won", "resolved")]))
            .Code.ShouldBe(VaultErrorCode.MutationBlocked);
    }

    /// <summary>
    /// Round three's first P0: byte equality is not ownership. A third
    /// writer whose bytes happen to MATCH the agent's planned edit is still
    /// a distinct inode belonging to someone else — under the old
    /// content-compare reclaim it was judged "ours" and deleted; the
    /// identity-led reclaim publishes it as the visible displaced sibling
    /// like any other third write.
    /// </summary>
    [Fact]
    public void A_byte_identical_third_write_is_still_theirs_and_survives_visibly()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        OnExchangeOf("Notes/a.md", p => ExternalReplace(p, "sync won\n"));
        // Same bytes as the agent's planned "agent update\n" — NEW inode.
        OnRacedExchangeOf("Notes/a.md", p => ExternalReplace(p, "agent update\n"));

        var ex = Should.Throw<KnapperException>(() =>
            _v.Service.Edit("Notes/a.md", sha, [new EditSpec("agent base", "agent update")]));

        ex.Code.ShouldBe(VaultErrorCode.PreconditionFailed);
        _v.ReadText("Notes/a.md").ShouldBe("sync won\n");
        var sibling = Directory.EnumerateFiles(Path.Combine(_v.VaultDir.Path, "Notes"))
            .Select(Path.GetFileName)
            .Where(n => n!.Contains(" (Knapper displaced", StringComparison.Ordinal))
            .ToList()
            .ShouldHaveSingleItem("their write survives visibly even when its bytes match ours");
        _v.ReadText("Notes/" + sibling).ShouldBe("agent update\n");
        _v.TempFiles().ShouldBeEmpty();
    }

    /// <summary>
    /// Round three's second P0: retention is the DEFAULT once the exchange
    /// has happened. An exception nobody anticipated — stood in for by the
    /// test seam itself throwing mid-undo — must leave the displaced
    /// external version on disk, not ride a false-by-default flag into the
    /// cleanup.
    /// </summary>
    [Fact]
    public void An_exception_during_the_undo_cannot_delete_the_displaced_version()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        OnExchangeOf("Notes/a.md", p => ExternalReplace(p, "sync won\n"));
        OnRacedExchangeOf("Notes/a.md", _ => throw new IOException("unanticipated failure mid-undo"));

        Should.Throw<KnapperException>(() =>
            _v.Service.Edit("Notes/a.md", sha, [new EditSpec("agent base", "agent update")]))
            .Code.ShouldBe(VaultErrorCode.IoError); // normalized unanticipated failure — not the point here

        var kept = _v.TempFiles().ShouldHaveSingleItem(
            "the displaced external version must survive an exceptional undo");
        _v.ReadText(kept).ShouldBe("sync won\n");
    }
}
