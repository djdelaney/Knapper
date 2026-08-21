using Knapper.Core.Mutation;
using Knapper.Core.Vault;

namespace Knapper.Core.Tests.Mutation;

/// <summary>
/// Parent-topology TOCTOU: <c>Resolve</c> rejects symlinked components and
/// returns an absolute STRING, and every later syscall re-walks that mutable
/// directory chain. A parent moved out of the vault and replaced by a symlink
/// after resolution used to let create/edit/append/batch write, verify, and
/// report success OUTSIDE the vault — and let move/delete capture (and on
/// success delete) an out-of-vault file — while the receipt named a vault
/// path. The window itself cannot be closed (even descriptor-relative ops
/// follow a moved directory); what these tests pin is the CONSEQUENCE:
/// containment is proved on both sides of every commit, so an escape becomes
/// a loud typed failure and never a success receipt, and nothing outside the
/// vault is ever removed.
///
/// <para>Shares the AtomicFile-hook collection with
/// <see cref="ReplaceCommitRaceTests"/>: the hooks are static, and two
/// parallel classes assigning them would silently overwrite each other.</para>
/// </summary>
[Collection("AtomicFile static test hooks")]
public sealed class ParentSwapTests : IDisposable
{
    private readonly MutationVault _v = new();

    public void Dispose()
    {
        AtomicFile.BeforeExchangeTestHook = null;
        AtomicFile.BeforeCreateLinkTestHook = null;
        _v.Dispose();
    }

    /// <summary>
    /// Move a vault directory outside and leave a symlink at its old name —
    /// the topology change nothing string-based can see.
    /// </summary>
    private string SwapOut(string relativeDir)
    {
        var moved = Path.Combine(_v.Outside.Path, "moved-" + relativeDir.Replace('/', '-'));
        Directory.Move(_v.Absolute(relativeDir), moved);
        Directory.CreateSymbolicLink(_v.Absolute(relativeDir), moved);
        return moved;
    }

    /// <summary>A gate whose under-lock pass performs the swap — the seam between Resolve and the write.</summary>
    private sealed class SwapOnSecondCall(Action swap) : ISyncGate
    {
        private int _calls;

        public void AssertMutationsAllowed()
        {
            if (Interlocked.Increment(ref _calls) == 2)
                swap();
        }
    }

    [Fact]
    public void An_edit_whose_parent_was_swapped_before_the_write_writes_nothing()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        string? moved = null;
        var service = _v.ServiceWithSyncGate(new SwapOnSecondCall(() => moved = SwapOut("Notes")));

        var ex = Should.Throw<KnapperException>(() =>
            service.Edit("Notes/a.md", sha, [new EditSpec("agent base", "agent update")]));

        ex.Code.ShouldBe(VaultErrorCode.PathOutsideVault);
        File.ReadAllText(Path.Combine(moved!, "a.md")).ShouldBe("agent base\n",
            "the pre-commit proof refuses before a single byte lands outside");
        Directory.EnumerateFiles(moved!, AtomicFile.TempPrefix + "*").ShouldBeEmpty();
    }

    /// <summary>
    /// The swap landing INSIDE the commit window: the write escapes — that
    /// cannot be prevented, only caught — and the post-commit proof is what
    /// turns it into a failure instead of a success receipt naming a vault
    /// path for bytes that live somewhere else.
    /// </summary>
    [Fact]
    public void An_edit_whose_parent_was_swapped_mid_write_cannot_report_success()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        // The resolver's canonical absolute, not TempDir's spelling.
        var target = _v.Resolver.Resolve("Notes/a.md").Absolute;
        string? moved = null;
        AtomicFile.BeforeExchangeTestHook = path =>
        {
            if (path == target)
                moved = SwapOut("Notes");
        };

        var ex = Should.Throw<KnapperException>(() =>
            _v.Service.Edit("Notes/a.md", sha, [new EditSpec("agent base", "agent update")]));

        ex.Code.ShouldBe(VaultErrorCode.PathOutsideVault);
        // The bytes DID land in the escaped directory — the invariant is not
        // "never escapes" but "an escape is never a success receipt".
        File.ReadAllText(Path.Combine(moved!, "a.md")).ShouldBe("agent update\n");
        Directory.EnumerateFiles(moved!, AtomicFile.TempPrefix + "*").ShouldBeEmpty();
        _v.AuditLines().ShouldContain(l => l.Contains("\"PathOutsideVault\""));
    }

    [Fact]
    public void A_create_whose_parent_was_swapped_mid_create_cannot_report_success()
    {
        _v.Service.CreateDirectory("Notes");
        var target = _v.Resolver.Resolve("Notes/new.md").Absolute;
        string? moved = null;
        AtomicFile.BeforeCreateLinkTestHook = path =>
        {
            if (path == target)
                moved = SwapOut("Notes");
        };

        var ex = Should.Throw<KnapperException>(() => _v.Service.Create("Notes/new.md", "fresh\n"));

        ex.Code.ShouldBe(VaultErrorCode.PathOutsideVault);
        File.ReadAllText(Path.Combine(moved!, "new.md")).ShouldBe("fresh\n");
        Directory.EnumerateFiles(moved!, AtomicFile.TempPrefix + "*").ShouldBeEmpty();
    }

    /// <summary>
    /// The source half of a move. Post-publish, the capture rename follows
    /// the swapped chain and takes a pathname OUTSIDE the vault — and the
    /// success path would then delete that capture. The post-capture proof
    /// fails first, and the rollback links the file straight back where the
    /// rename took it from. The destination stays published (never
    /// retracted): a visible duplicate, disclosed by the error.
    /// </summary>
    [Fact]
    public void A_move_whose_source_parent_was_swapped_after_publish_removes_nothing_outside()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        _v.Service.CreateDirectory("Archive");
        string? moved = null;
        _v.Service.BeforeCaptureTestHook = (_, _) => moved = SwapOut("Notes");

        var ex = Should.Throw<KnapperException>(() =>
            _v.Service.Move("Notes/a.md", "Archive/b.md", sha));

        ex.Code.ShouldBe(VaultErrorCode.PathOutsideVault);
        File.ReadAllText(Path.Combine(moved!, "a.md")).ShouldBe("agent base\n",
            "the captured out-of-vault file must be restored, not deleted");
        _v.ReadText("Archive/b.md").ShouldBe("agent base\n", "a published destination is never retracted");
        Directory.EnumerateFiles(moved!, AtomicFile.TempPrefix + "*").ShouldBeEmpty();
    }

    [Fact]
    public void A_delete_whose_source_parent_was_swapped_after_publish_removes_nothing_outside()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        string? moved = null;
        _v.Service.BeforeCaptureTestHook = (_, _) => moved = SwapOut("Notes");

        var ex = Should.Throw<KnapperException>(() => _v.Service.Delete("Notes/a.md", sha));

        ex.Code.ShouldBe(VaultErrorCode.PathOutsideVault);
        File.ReadAllText(Path.Combine(moved!, "a.md")).ShouldBe("agent base\n");
        _v.ReadText(".trash/Notes/a.md").ShouldBe("agent base\n", "the trash entry stays published");
        Directory.EnumerateFiles(moved!, AtomicFile.TempPrefix + "*").ShouldBeEmpty();
    }
}
