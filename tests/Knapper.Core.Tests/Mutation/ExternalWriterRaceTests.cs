using Knapper.Core.Mutation;

namespace Knapper.Core.Tests.Mutation;

/// <summary>
/// Move and soft delete are link-then-unlink; an EXTERNAL writer (Sync, a
/// human shell — nothing that honors our locks) can replace the source in
/// the read→link or link→unlink window. A failed operation must leave no
/// new destination and no new .trash entry, and must never destroy the
/// external writer's replacement. The test hooks run inside the critical
/// section and stand in for that writer deterministically.
/// </summary>
public sealed class ExternalWriterRaceTests : IDisposable
{
    private readonly MutationVault _v = new();

    public void Dispose() => _v.Dispose();

    /// <summary>Replace like Sync does: write a temp sibling, rename over — a NEW inode.</summary>
    private void ExternalReplace(string absolutePath, string newContent)
    {
        var temp = absolutePath + ".sync-replace";
        File.WriteAllText(temp, newContent);
        File.Move(temp, absolutePath, overwrite: true);
    }

    [Fact]
    public void Move_racing_a_replace_before_the_link_fails_and_leaves_no_destination()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        _v.Service.BeforeLinkTestHook = src => ExternalReplace(src, "sync won\n");

        Should.Throw<KnapperException>(() => _v.Service.Move("Notes/a.md", "Notes/b.md", sha));

        File.Exists(Path.Combine(_v.VaultDir.Path, "Notes/b.md"))
            .ShouldBeFalse("a failed move must leave no destination behind");
        _v.ReadText("Notes/a.md").ShouldBe("sync won\n", "the external write must survive");
    }

    [Fact]
    public void Move_racing_a_replace_after_the_link_rolls_back_and_preserves_the_external_write()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        _v.Service.AfterLinkTestHook = src => ExternalReplace(src, "sync won\n");

        var ex = Should.Throw<KnapperException>(() => _v.Service.Move("Notes/a.md", "Notes/b.md", sha));
        ex.Code.ShouldBe(VaultErrorCode.PreconditionFailed);

        File.Exists(Path.Combine(_v.VaultDir.Path, "Notes/b.md"))
            .ShouldBeFalse("the rolled-back move must remove its link");
        _v.ReadText("Notes/a.md").ShouldBe("sync won\n",
            "unlinking the source would have silently destroyed the external write");
    }

    [Fact]
    public void Delete_racing_a_replace_before_the_link_fails_and_leaves_no_trash_entry()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        _v.Service.BeforeLinkTestHook = src => ExternalReplace(src, "sync won\n");

        Should.Throw<KnapperException>(() => _v.Service.Delete("Notes/a.md", sha));

        // No trash FILE may remain (an empty directory skeleton is invisible
        // to queries and gets reused by later deletes — not residue).
        var trashRoot = Path.Combine(_v.VaultDir.Path, ".trash");
        (Directory.Exists(trashRoot)
                ? Directory.EnumerateFiles(trashRoot, "*", SearchOption.AllDirectories)
                : [])
            .ShouldBeEmpty("a failed delete must leave no stray .trash entry");
        _v.ReadText("Notes/a.md").ShouldBe("sync won\n");
    }

    [Fact]
    public void Delete_racing_a_replace_after_the_link_rolls_back_and_preserves_the_external_write()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        _v.Service.AfterLinkTestHook = src => ExternalReplace(src, "sync won\n");

        var ex = Should.Throw<KnapperException>(() => _v.Service.Delete("Notes/a.md", sha));
        ex.Code.ShouldBe(VaultErrorCode.PreconditionFailed);

        File.Exists(Path.Combine(_v.VaultDir.Path, ".trash/Notes/a.md"))
            .ShouldBeFalse("the rolled-back delete must remove its trash link");
        _v.ReadText("Notes/a.md").ShouldBe("sync won\n");
    }

    [Fact]
    public void A_file_past_the_read_cap_moves_and_soft_deletes_with_its_stat_sha()
    {
        _v.Write("attachments/big.bin", new string('x', 4096));
        var capped = new Knapper.Core.Query.VaultReadService(
            _v.Resolver, new Knapper.Core.Options.VaultOptions { MaxReadBytes = 16 }, _v.Generation);

        var sha = capped.Stat("attachments/big.bin").Sha256!;
        _v.Service.CreateDirectory("archive");
        _v.Service.Move("attachments/big.bin", "archive/big.bin", sha);

        var movedSha = capped.Stat("archive/big.bin").Sha256!;
        movedSha.ShouldBe(sha);
        var result = _v.Service.Delete("archive/big.bin", movedSha);
        _v.ReadText(result.TrashPath).ShouldBe(new string('x', 4096));
    }

    [Fact]
    public void Unraced_move_and_delete_still_complete()
    {
        var shaA = _v.Write("Notes/a.md", "content a\n");
        _v.Service.Move("Notes/a.md", "Notes/b.md", shaA);
        _v.ReadText("Notes/b.md").ShouldBe("content a\n");
        File.Exists(Path.Combine(_v.VaultDir.Path, "Notes/a.md")).ShouldBeFalse();

        var shaB = _v.Write("Notes/c.md", "content c\n");
        var result = _v.Service.Delete("Notes/c.md", shaB);
        _v.ReadText(result.TrashPath).ShouldBe("content c\n");
        File.Exists(Path.Combine(_v.VaultDir.Path, "Notes/c.md")).ShouldBeFalse();
    }
}
