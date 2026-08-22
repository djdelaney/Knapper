using Knapper.Core.Vault;

namespace Knapper.Core.Tests.Mutation;

/// <summary>
/// The window between the last look at the source and the moment its
/// pathname is taken.
///
/// <para>Under the previous check-then-delete design this window was
/// unfixable by construction: the source was re-verified by content and then
/// <c>File.Delete</c>d, so an external writer replacing it in between had its
/// write destroyed — and because the destination still held the agent's old
/// bytes, the final verification passed and the move reported SUCCESS. Found
/// in review 2026-08-19 with exactly the reproduction below.</para>
///
/// <para>The fix is not another check. The source pathname is CAPTURED with
/// rename(2) — atomic, so nothing is removed on the strength of a check that
/// has already expired — and what was captured is examined afterwards, under
/// a private name. A capture that turns out to be somebody else's write is
/// linked straight back.</para>
/// </summary>
public sealed class SourceCaptureRaceTests : IDisposable
{
    private readonly MutationVault _v = new();

    public void Dispose() => _v.Dispose();

    /// <summary>Replace like Sync does: write a temp sibling, rename over — a NEW inode.</summary>
    private static void ExternalReplace(string absolutePath, string newContent)
    {
        var temp = absolutePath + ".sync-replace";
        File.WriteAllText(temp, newContent);
        File.Move(temp, absolutePath, overwrite: true);
    }

    /// <summary>
    /// The courtesy check must classify an UNREADABLE source as "not ours",
    /// like its two sibling judgements do. The replacement lands between the
    /// link and the courtesy read, so the hard-linked temp still holds the
    /// original inode and verifies fine while the SOURCE PATHNAME now names
    /// a different, mode-000 file — the shape that separates the two.
    ///
    /// <para><c>File.ReadAllBytes</c> answers
    /// <c>UnauthorizedAccessException</c> there, not <c>IOException</c>, so a
    /// handler listing only <c>KnapperException or IOException</c> let it
    /// escape: nothing was destroyed (the rollback catch is exhaustive by
    /// construction) but the agent was told <c>[IoError]</c>, "filesystem
    /// failure", for what is a plain lost race. An agent parses that code to
    /// choose between re-reading and giving up, and only
    /// <c>[PreconditionFailed]</c> tells it the true thing. Unreadable means
    /// the source cannot be PROVEN to still hold our bytes, and unprovable is
    /// what this precondition exists to reject.</para>
    /// </summary>
    [Fact]
    public void A_source_replaced_by_an_unreadable_file_fails_the_precondition_not_the_filesystem()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        _v.Service.AfterLinkTestHook = source =>
        {
            ExternalReplace(source, "sync replacement\n");
            File.SetUnixFileMode(source, UnixFileMode.None);
        };

        try
        {
            var ex = Should.Throw<KnapperException>(() => _v.Service.Move("Notes/a.md", "Notes/b.md", sha));

            ex.Code.ShouldBe(VaultErrorCode.PreconditionFailed,
                "an unreadable source is a lost race, not a filesystem failure");
            File.Exists(_v.Absolute("Notes/b.md")).ShouldBeFalse(
                "the courtesy check runs BEFORE the publish — nothing may be published");
            _v.TempFiles().ShouldBeEmpty("no unexplained temp may be left behind");
        }
        finally
        {
            // Restore readability or TempDir.Dispose cannot clean up.
            File.SetUnixFileMode(_v.Absolute("Notes/a.md"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>
    /// The original reproduction. The bug it pins: the source was re-verified
    /// and then File.Delete'd, so the replacement was destroyed and the move
    /// reported SUCCESS.
    ///
    /// <para>The destination IS published here, and stays. That is the
    /// sanctioned cost of publishing before capturing: the alternative is
    /// retracting a pathname other writers can already see, which is how
    /// their data gets destroyed. The stray is named in the error so a human
    /// can remove it, and the operation still fails.</para>
    /// </summary>
    [Fact]
    public void A_source_replaced_in_the_last_instant_survives_a_move_and_the_move_fails()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        _v.Service.BeforeCaptureTestHook = (source, _) => ExternalReplace(source, "sync replacement\n");

        var ex = Should.Throw<KnapperException>(() => _v.Service.Move("Notes/a.md", "Notes/b.md", sha));
        ex.Code.ShouldBe(VaultErrorCode.PreconditionFailed);
        ex.Message.ShouldContain("b.md"); // the un-retracted duplicate is named

        _v.ReadText("Notes/a.md").ShouldBe("sync replacement\n",
            "the external writer's bytes must survive — deleting them and reporting success was the bug");
        _v.ReadText("Notes/b.md").ShouldBe("agent base\n",
            "the published destination is not retracted; it is reported instead");
        _v.TempFiles().ShouldBeEmpty("no unexplained temp may be left behind");
    }

    [Fact]
    public void A_source_replaced_in_the_last_instant_survives_a_delete_and_the_delete_fails()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        _v.Service.BeforeCaptureTestHook = (source, _) => ExternalReplace(source, "sync replacement\n");

        Should.Throw<KnapperException>(() => _v.Service.Delete("Notes/a.md", sha))
            .Code.ShouldBe(VaultErrorCode.PreconditionFailed);

        _v.ReadText("Notes/a.md").ShouldBe("sync replacement\n");
        _v.ReadText(".trash/Notes/a.md").ShouldBe("agent base\n",
            "the trash entry was already published and is not retracted");
        _v.TempFiles().ShouldBeEmpty();
    }

    /// <summary>
    /// The same window, but the external writer REMOVES the source instead of
    /// replacing it. The capture fails outright; nothing may be published and
    /// nothing may be left behind.
    /// </summary>
    [Fact]
    public void A_source_removed_in_the_last_instant_fails_the_move_cleanly()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        _v.Service.BeforeCaptureTestHook = (source, _) => File.Delete(source);

        Should.Throw<KnapperException>(() => _v.Service.Move("Notes/a.md", "Notes/b.md", sha))
            .Code.ShouldBe(VaultErrorCode.NotFound);

        _v.ReadText("Notes/b.md").ShouldBe("agent base\n", "the content still has a public home");
        _v.TempFiles().ShouldBeEmpty();
    }

    /// <summary>
    /// The captured file is restored to its OWN pathname, not written over
    /// whatever is there now: if another writer has taken the source name in
    /// the meantime, the restore is refused and what we captured is kept
    /// under a hidden name the error points at. Nothing is destroyed in
    /// either direction.
    /// </summary>
    [Fact]
    public void A_capture_that_cannot_be_restored_is_kept_and_named()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        // The external writer replaces the source (so the capture is theirs,
        // not ours) and then takes the pathname the moment the capture frees
        // it — so their write can be put back nowhere.
        _v.Service.BeforeCaptureTestHook = (source, _) => ExternalReplace(source, "sync replacement\n");
        _v.Service.AfterCaptureTestHook = (source, _) =>
            File.WriteAllText(source, "and a third writer at the old name\n");

        var ex = Should.Throw<KnapperException>(() => _v.Service.Move("Notes/a.md", "Notes/b.md", sha));
        ex.Code.ShouldBe(VaultErrorCode.VerifyFailed);
        ex.Message.ShouldContain(AtomicFile.TempPrefix);

        _v.ReadText("Notes/a.md").ShouldBe("and a third writer at the old name\n", "we never overwrite");
        _v.ReadText("Notes/b.md").ShouldBe("agent base\n", "the published copy of the original stands");
        var temps = _v.TempFiles();
        temps.Length.ShouldBe(1, "what was captured must be kept when it cannot be put back");
        _v.ReadText(temps[0]).ShouldBe("sync replacement\n", "and it is THEIR write we are holding");
    }

    [Fact]
    public void An_unraced_move_and_delete_still_complete()
    {
        var shaA = _v.Write("Notes/a.md", "content a\n");
        _v.Service.Move("Notes/a.md", "Notes/b.md", shaA);
        _v.ReadText("Notes/b.md").ShouldBe("content a\n");
        File.Exists(_v.Absolute("Notes/a.md")).ShouldBeFalse();

        var shaB = _v.Write("Notes/c.md", "content c\n");
        var result = _v.Service.Delete("Notes/c.md", shaB);
        _v.ReadText(result.TrashPath).ShouldBe("content c\n");
        _v.TempFiles().ShouldBeEmpty();
    }
}
