using Knapper.Core.Mutation;
using Knapper.Core.Vault;

namespace Knapper.Core.Tests.Mutation;

/// <summary>
/// The FINAL-COMPONENT symlink race (reviewer follow-up, 2026-08-20,
/// reproduced here before the fix): `Resolve` rejects symlinks, but an
/// external writer can replace the note itself with an equal-content symlink
/// after resolution. Plain link(2) then diverged by platform — macOS FOLLOWED
/// the symlink and hard-linked the OUT-OF-VAULT target into the vault (the
/// published note shared an inode with an outside file, and the success path
/// deleted the external writer's symlink), while Linux would publish the
/// symlink itself into a vault that bans them. The fix is one primitive plus
/// one inspection: the source is linked with linkat(…, 0) — NO-FOLLOW on both
/// platforms, so whatever the final component is gets captured AS-IS under
/// Knapper's private name — and that private name is then inspected with
/// non-following metadata and refused before anything is published. The
/// capture-side twin: <c>CapturedIsOurs</c> and <c>TryRestoreSource</c> treat
/// a captured symlink as not-ours and restore it AS a symlink.
///
/// <para>Equal content everywhere, deliberately: these races are only won by
/// the non-following checks — any test passing on a content mismatch would
/// prove nothing.</para>
///
/// <para>Shares the AtomicFile-hook collection: the Replace test assigns the
/// static hook, and parallel classes would overwrite each other's.</para>
/// </summary>
[Collection("AtomicFile static test hooks")]
public sealed class SymlinkSwapRaceTests : IDisposable
{
    private readonly MutationVault _v = new();
    private readonly string _outside;

    public SymlinkSwapRaceTests()
    {
        _outside = Path.Combine(_v.Outside.Path, "outside.md");
        File.WriteAllText(_outside, "agent base\n");
    }

    public void Dispose()
    {
        AtomicFile.BeforeExchangeTestHook = null;
        AtomicFile.AfterRacedExchangeTestHook = null;
        _v.Dispose();
    }

    private void SwapForSymlink(string absolutePath)
    {
        File.Delete(absolutePath);
        File.CreateSymbolicLink(absolutePath, _outside);
    }

    private static bool IsSymlink(string absolutePath) => new FileInfo(absolutePath).LinkTarget is not null;

    [Fact]
    public void A_move_source_swapped_for_an_equal_content_symlink_is_refused_untouched()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        _v.Service.CreateDirectory("Archive");
        _v.Service.BeforeLinkTestHook = SwapForSymlink;

        var ex = Should.Throw<KnapperException>(() =>
            _v.Service.Move("Notes/a.md", "Archive/b.md", sha));

        // Before the fix this returned SUCCESS with Archive/b.md sharing an
        // inode with the outside file and the external symlink deleted.
        ex.Code.ShouldBe(VaultErrorCode.SymlinkRejected);
        File.Exists(_v.Absolute("Archive/b.md")).ShouldBeFalse("nothing may be published");
        IsSymlink(_v.Absolute("Notes/a.md")).ShouldBeTrue("their symlink is not ours to remove");
        File.ReadAllText(_outside).ShouldBe("agent base\n");
        _v.TempFiles().ShouldBeEmpty();
    }

    [Fact]
    public void A_delete_source_swapped_for_an_equal_content_symlink_is_refused_untouched()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        _v.Service.BeforeLinkTestHook = SwapForSymlink;

        var ex = Should.Throw<KnapperException>(() => _v.Service.Delete("Notes/a.md", sha));

        ex.Code.ShouldBe(VaultErrorCode.SymlinkRejected);
        var trashRoot = Path.Combine(_v.VaultDir.Path, ".trash");
        (Directory.Exists(trashRoot)
                ? Directory.EnumerateFiles(trashRoot, "*", SearchOption.AllDirectories)
                : [])
            .ShouldBeEmpty("no trash entry may be published");
        IsSymlink(_v.Absolute("Notes/a.md")).ShouldBeTrue();
        _v.TempFiles().ShouldBeEmpty();
    }

    /// <summary>
    /// The capture-side window: the swap lands after the destination is
    /// published, so the capture rename takes the SYMLINK. Reading through
    /// it would call it "ours" (equal content) and the success path would
    /// delete it; the non-following check routes to the rollback instead,
    /// which restores the symlink AS a symlink (plain link(2) on macOS would
    /// have planted a hard link to the outside target instead).
    /// </summary>
    [Fact]
    public void A_source_swapped_for_a_symlink_after_publish_is_restored_not_deleted()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        _v.Service.CreateDirectory("Archive");
        _v.Service.BeforeCaptureTestHook = (source, _) => SwapForSymlink(source);

        var ex = Should.Throw<KnapperException>(() =>
            _v.Service.Move("Notes/a.md", "Archive/b.md", sha));

        ex.Code.ShouldBe(VaultErrorCode.PreconditionFailed);
        IsSymlink(_v.Absolute("Notes/a.md")).ShouldBeTrue("the captured symlink must come back as itself");
        _v.ReadText("Archive/b.md").ShouldBe("agent base\n", "a published destination is never retracted");
        File.ReadAllText(_outside).ShouldBe("agent base\n");
        _v.TempFiles().ShouldBeEmpty();
    }

    /// <summary>
    /// The replace twin: the target becomes an equal-content symlink in the
    /// final window. The exchange captures the symlink under the private
    /// name, the non-following judgement refuses to read through it, and the
    /// swap-back puts their symlink back — clean rejection, nothing of ours
    /// canonical, nothing of theirs destroyed or aliased.
    /// </summary>
    [Fact]
    public void An_edit_whose_target_became_an_equal_content_symlink_rejects_and_restores_it()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        var target = _v.Resolver.Resolve("Notes/a.md").Absolute;
        AtomicFile.BeforeExchangeTestHook = path =>
        {
            if (path == target)
                SwapForSymlink(path);
        };

        var ex = Should.Throw<KnapperException>(() =>
            _v.Service.Edit("Notes/a.md", sha, [new EditSpec("agent base", "agent update")]));

        ex.Code.ShouldBe(VaultErrorCode.PreconditionFailed);
        IsSymlink(_v.Absolute("Notes/a.md")).ShouldBeTrue("their symlink is restored, not judged through");
        File.ReadAllText(_outside).ShouldBe("agent base\n", "the outside target is never written");
        _v.TempFiles().ShouldBeEmpty();
    }

    // ---- round three: the RECOVERY branches must not follow either ----------

    /// <summary>
    /// The displaced object is a symlink AND the swap-back cannot run because
    /// the canonical name was deleted in between. Round three reproduced the
    /// restore link FOLLOWING the symlink on macOS — recreating the exact
    /// outside-inode alias the main path had just been cured of. With the
    /// no-follow restore, the symlink comes back as itself.
    /// </summary>
    [Fact]
    public void A_displaced_symlink_with_a_deleted_canonical_name_is_restored_as_a_symlink()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        var target = _v.Resolver.Resolve("Notes/a.md").Absolute;
        AtomicFile.BeforeExchangeTestHook = path =>
        {
            if (path == target)
                SwapForSymlink(path);
        };
        AtomicFile.AfterRacedExchangeTestHook = path =>
        {
            if (path == target)
                File.Delete(path);
        };

        var ex = Should.Throw<KnapperException>(() =>
            _v.Service.Edit("Notes/a.md", sha, [new EditSpec("agent base", "agent update")]));

        ex.Code.ShouldBe(VaultErrorCode.PreconditionFailed);
        IsSymlink(_v.Absolute("Notes/a.md")).ShouldBeTrue(
            "the restore must not follow the displaced symlink into a hard-link alias");
        File.ReadAllText(_outside).ShouldBe("agent base\n");
        _v.TempFiles().ShouldBeEmpty();
    }

    /// <summary>
    /// The displaced object is a symlink and a THIRD write lands between the
    /// exchanges: the swap-back restores the symlink canonically (as itself),
    /// and the third write becomes the visible displaced sibling — published
    /// no-follow, so nothing anywhere aliases the outside inode.
    /// </summary>
    [Fact]
    public void A_displaced_symlink_with_a_third_write_keeps_every_shape_intact()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        var target = _v.Resolver.Resolve("Notes/a.md").Absolute;
        AtomicFile.BeforeExchangeTestHook = path =>
        {
            if (path == target)
                SwapForSymlink(path);
        };
        AtomicFile.AfterRacedExchangeTestHook = path =>
        {
            if (path != target)
                return;
            var temp = path + ".sync-replace";
            File.WriteAllText(temp, "sync won again\n");
            File.Move(temp, path, overwrite: true);
        };

        var ex = Should.Throw<KnapperException>(() =>
            _v.Service.Edit("Notes/a.md", sha, [new EditSpec("agent base", "agent update")]));

        ex.Code.ShouldBe(VaultErrorCode.PreconditionFailed);
        ex.Message.ShouldContain("Knapper displaced");
        IsSymlink(_v.Absolute("Notes/a.md")).ShouldBeTrue("the earlier external version — the symlink — is canonical again");
        var sibling = Directory.EnumerateFiles(Path.Combine(_v.VaultDir.Path, "Notes"))
            .Select(Path.GetFileName)
            .Where(n => n!.Contains(" (Knapper displaced", StringComparison.Ordinal))
            .ToList()
            .ShouldHaveSingleItem();
        IsSymlink(_v.Absolute("Notes/" + sibling)).ShouldBeFalse("the third write is a regular file");
        _v.ReadText("Notes/" + sibling).ShouldBe("sync won again\n");
        File.ReadAllText(_outside).ShouldBe("agent base\n");
        _v.TempFiles().ShouldBeEmpty();
    }

    // ---- round three P2: non-regular files must refuse, never hang ----------

    private void MakeFifo(string absolutePath)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("mkfifo");
        psi.ArgumentList.Add(absolutePath);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
        p.ExitCode.ShouldBe(0, "mkfifo must be available for the FIFO race tests");
    }

    private static async Task<KnapperException> BoundedThrow(Action operation)
    {
        // The bound IS the assertion: a FIFO read blocks forever holding the
        // path locks, so a hang here is the defect, not a slow test.
        var work = Task.Run(() => Should.Throw<KnapperException>(operation));
        var winner = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(15)));
        winner.ShouldBe(work, "the mutation must refuse the non-regular file, not block on it");
        return await work;
    }

    [Fact]
    public async Task A_move_source_swapped_for_a_fifo_is_refused_without_blocking()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        _v.Service.CreateDirectory("Archive");
        _v.Service.BeforeLinkTestHook = source =>
        {
            File.Delete(source);
            MakeFifo(source);
        };

        var ex = await BoundedThrow(() => _v.Service.Move("Notes/a.md", "Archive/b.md", sha));

        ex.Code.ShouldBe(VaultErrorCode.PreconditionFailed);
        File.Exists(_v.Absolute("Archive/b.md")).ShouldBeFalse("nothing may be published");
        var fifo = Knapper.Core.Interop.Posix.LStat(_v.Absolute("Notes/a.md"));
        (fifo.IsRegular || fifo.IsSymlink).ShouldBeFalse("their FIFO is not ours to remove");
        _v.TempFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task An_edit_whose_target_became_a_fifo_is_refused_without_blocking()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        var target = _v.Resolver.Resolve("Notes/a.md").Absolute;
        AtomicFile.BeforeExchangeTestHook = path =>
        {
            if (path != target)
                return;
            File.Delete(path);
            MakeFifo(path);
        };

        var ex = await BoundedThrow(() =>
            _v.Service.Edit("Notes/a.md", sha, [new EditSpec("agent base", "agent update")]));

        ex.Code.ShouldBe(VaultErrorCode.PreconditionFailed);
        var restored = Knapper.Core.Interop.Posix.LStat(_v.Absolute("Notes/a.md"));
        (restored.IsRegular || restored.IsSymlink).ShouldBeFalse("their FIFO is restored by the swap-back");
        _v.TempFiles().ShouldBeEmpty();
    }

    [Fact]
    public async Task An_edit_of_a_path_holding_a_fifo_is_refused_without_blocking()
    {
        // The FIFO is at the pathname before the mutation even starts — the
        // first read of the critical section is the surface that must refuse.
        _v.Write("Notes/a.md", "agent base\n");
        var sha = Knapper.Core.Vault.VaultHash.Sha256Hex("agent base\n"u8.ToArray());
        File.Delete(_v.Absolute("Notes/a.md"));
        MakeFifo(_v.Absolute("Notes/a.md"));

        var ex = await BoundedThrow(() =>
            _v.Service.Edit("Notes/a.md", sha, [new EditSpec("agent base", "agent update")]));

        ex.Code.ShouldBe(VaultErrorCode.PreconditionFailed);
    }
}
