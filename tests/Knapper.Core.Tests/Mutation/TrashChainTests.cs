using Knapper.Core.Vault;

namespace Knapper.Core.Tests.Mutation;

/// <summary>
/// Soft delete is the one path where Knapper builds a vault path itself: the
/// source goes through <see cref="VaultPathResolver"/>, but `.trash/` +
/// source-relative is assembled by hand, because `.trash` is deliberately
/// unaddressable and can never come back out of the resolver.
///
/// <para>That left the trash chain unchecked while the source chain was
/// checked at every component. A symlink at `.trash` — or at any directory
/// under it — sends <c>link(2)</c> outside the vault entirely, and the source
/// is unlinked immediately afterwards: the note leaves the vault Knapper
/// serves, leaves git, leaves every backup assumption, and the receipt still
/// says `.trash/...`. Agents cannot plant that symlink (dot segments are
/// unaddressable), so the actor is a human or another process on the box —
/// which is precisely the writer the rest of this layer is built around.</para>
/// </summary>
public sealed class TrashChainTests : IDisposable
{
    private readonly MutationVault _v = new();
    private readonly TempDir _outside = new();

    public void Dispose()
    {
        _outside.Dispose();
        _v.Dispose();
    }

    private string Outside(string name) => Path.Combine(_outside.Path, name);

    /// <summary>Nothing may have escaped: the vault's own copy stays, the outside stays empty.</summary>
    private void ShouldHaveRefusedWithoutTouchingAnything(string relative, string content)
    {
        _v.ReadText(relative).ShouldBe(content, "a refused delete must leave the source exactly as it was");
        Directory.EnumerateFiles(_outside.Path, "*", SearchOption.AllDirectories)
            .ShouldBeEmpty("nothing may be linked outside the vault");
    }

    [Fact]
    public void A_trash_root_that_is_a_symlink_out_of_the_vault_is_refused()
    {
        var sha = _v.Write("Notes/a.md", "private\n");
        Directory.CreateDirectory(Outside("stolen"));
        Directory.CreateSymbolicLink(_v.Absolute(".trash"), Outside("stolen"));

        Should.Throw<KnapperException>(() => _v.Service.Delete("Notes/a.md", sha))
            .Code.ShouldBe(VaultErrorCode.SymlinkRejected);

        ShouldHaveRefusedWithoutTouchingAnything("Notes/a.md", "private\n");
    }

    [Fact]
    public void A_preserved_subdirectory_that_is_a_symlink_out_of_the_vault_is_refused()
    {
        var sha = _v.Write("Notes/a.md", "private\n");
        Directory.CreateDirectory(Outside("stolen"));
        Directory.CreateDirectory(_v.Absolute(".trash"));
        Directory.CreateSymbolicLink(_v.Absolute(".trash/Notes"), Outside("stolen"));

        Should.Throw<KnapperException>(() => _v.Service.Delete("Notes/a.md", sha))
            .Code.ShouldBe(VaultErrorCode.SymlinkRejected);

        ShouldHaveRefusedWithoutTouchingAnything("Notes/a.md", "private\n");
    }

    /// <summary>
    /// A symlink that resolves to nothing is still a symlink. It is rejected
    /// by asking whether the component IS a link, not whether it resolves —
    /// otherwise the answer would ride on whether File.Exists follows links,
    /// which is a runtime and platform detail.
    /// </summary>
    [Fact]
    public void A_dangling_symlink_in_the_trash_chain_is_refused()
    {
        var sha = _v.Write("Notes/a.md", "private\n");
        Directory.CreateDirectory(_v.Absolute(".trash"));
        Directory.CreateSymbolicLink(_v.Absolute(".trash/Notes"), Outside("never-created"));

        Should.Throw<KnapperException>(() => _v.Service.Delete("Notes/a.md", sha))
            .Code.ShouldBe(VaultErrorCode.SymlinkRejected);

        ShouldHaveRefusedWithoutTouchingAnything("Notes/a.md", "private\n");
    }

    /// <summary>
    /// A symlink even inside the vault is refused: the rule is the resolver's
    /// rule, and it does not ask where the link points. Two spellings of one
    /// directory is exactly what per-path locking cannot serialize.
    /// </summary>
    [Fact]
    public void A_symlink_inside_the_vault_is_refused_too()
    {
        var sha = _v.Write("Notes/a.md", "private\n");
        Directory.CreateDirectory(_v.Absolute(".trash"));
        Directory.CreateDirectory(_v.Absolute("Elsewhere"));
        Directory.CreateSymbolicLink(_v.Absolute(".trash/Notes"), _v.Absolute("Elsewhere"));

        Should.Throw<KnapperException>(() => _v.Service.Delete("Notes/a.md", sha))
            .Code.ShouldBe(VaultErrorCode.SymlinkRejected);

        _v.ReadText("Notes/a.md").ShouldBe("private\n");
        Directory.EnumerateFiles(_v.Absolute("Elsewhere")).ShouldBeEmpty();
    }

    /// <summary>
    /// The chain check is walked twice — the second time after
    /// Directory.CreateDirectory, which follows an existing directory
    /// symlink — but a check against a directory chain holds only until the
    /// next instant. This drives the swap into the last window there is,
    /// between the final check and link(2), which nothing short of
    /// descriptor-relative linkat can close.
    ///
    /// <para>What must hold anyway: the link is caught by the containment
    /// check BEFORE the source is unlinked, so the escape is rolled back and
    /// the note is still under its own name. The failure this prevents is not
    /// "a link briefly existed outside the vault" — it is a note that left
    /// the vault permanently while the receipt said `.trash/...`.</para>
    /// </summary>
    [Fact]
    public void A_symlink_that_appears_in_the_last_instant_is_caught_before_the_source_is_unlinked()
    {
        Directory.CreateDirectory(Outside("stolen"));
        var probe = _v.Write("Notes/probe.md", "probe\n");
        _v.Service.Delete("Notes/probe.md", probe); // makes .trash/Notes a real directory

        var sha = _v.Write("Notes/a.md", "private\n");
        _v.Service.BeforeLinkTestHook = _ =>
        {
            Directory.Delete(_v.Absolute(".trash/Notes"), recursive: true);
            Directory.CreateSymbolicLink(_v.Absolute(".trash/Notes"), Outside("stolen"));
        };

        Should.Throw<KnapperException>(() => _v.Service.Delete("Notes/a.md", sha))
            .Code.ShouldBe(VaultErrorCode.PathOutsideVault);

        _v.ReadText("Notes/a.md").ShouldBe("private\n", "the source must survive an escaped destination");
        Directory.EnumerateFiles(_outside.Path, "*", SearchOption.AllDirectories)
            .ShouldBeEmpty("the escaped link is rolled back");
        _v.TempFiles().ShouldBeEmpty();
    }

    // ---- the ordinary paths still work -------------------------------------

    [Fact]
    public void An_ordinary_soft_delete_still_preserves_structure()
    {
        var sha = _v.Write("Notes/Sub/a.md", "content\n");
        var result = _v.Service.Delete("Notes/Sub/a.md", sha);

        result.TrashPath.ShouldBe(".trash/Notes/Sub/a.md");
        _v.ReadText(result.TrashPath).ShouldBe("content\n");
        File.Exists(_v.Absolute("Notes/Sub/a.md")).ShouldBeFalse();
    }

    [Fact]
    public void A_second_delete_of_the_same_path_is_timestamped_and_keeps_both()
    {
        var first = _v.Write("Notes/a.md", "first\n");
        _v.Service.Delete("Notes/a.md", first);
        var second = _v.Write("Notes/a.md", "second\n");
        var result = _v.Service.Delete("Notes/a.md", second);

        result.TrashPath.ShouldNotBe(".trash/Notes/a.md");
        _v.ReadText(".trash/Notes/a.md").ShouldBe("first\n", "an earlier trash copy is never overwritten");
        _v.ReadText(result.TrashPath).ShouldBe("second\n");
    }
}
