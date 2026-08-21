using Knapper.Core.Vault;

namespace Knapper.Core.Tests.Mutation;

/// <summary>
/// The DESTINATION half of the races. <see cref="ExternalWriterRaceTests"/>
/// and <see cref="SourceCaptureRaceTests"/> aim an external writer at the
/// source; these aim it at the pathname move and delete publish.
///
/// <para>The property under test throughout: <b>Knapper never removes a
/// pathname another writer could own.</b> The only names it deletes are its
/// own hidden temps, under GUIDs nobody else can know — so "is this still our
/// link?" is never a question a rollback has to answer by guessing, and a
/// replacement at the destination survives whether or not its bytes happen to
/// match ours.</para>
///
/// <para>Every test asserts the CONTENTS and existence of both pathnames, not
/// just the error code: an operation that reports the right failure while
/// losing a file passes a code-only assertion.</para>
///
/// <para><b>The commit boundary</b> (see `LinkPublishCapture`): the
/// destination being published and VERIFIED is the commit. An external
/// writer attacking the destination BEFORE that point makes the operation
/// fail with everything preserved; attacking it AFTER that point is an
/// ordinary external delete/overwrite of the note — the operation reports
/// success and the external action stands, exactly as if it had landed a
/// millisecond after the operation returned. Both sides are pinned below;
/// the after-capture tests are the boundary's far side.</para>
/// </summary>
public sealed class DestinationRaceTests : IDisposable
{
    private readonly MutationVault _v = new();

    public void Dispose() => _v.Dispose();

    private static void ExternalReplace(string absolutePath, string newContent)
    {
        var temp = absolutePath + ".sync-replace";
        File.WriteAllText(temp, newContent);
        File.Move(temp, absolutePath, overwrite: true);
    }

    // ---- destination taken BEFORE the commit: no-clobber refuses it ---------

    /// <summary>
    /// The commit is a link(2), so a destination that appeared after the
    /// existence check is refused by the kernel rather than replaced — and
    /// the rollback that follows removes nothing of theirs.
    /// </summary>
    [Fact]
    public void A_destination_taken_before_the_commit_is_refused_and_left_alone()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        _v.Service.BeforeCommitTestHook = (_, destination) =>
            File.WriteAllText(destination, "sync got here first\n");

        Should.Throw<KnapperException>(() => _v.Service.Move("Notes/a.md", "Notes/b.md", sha))
            .Code.ShouldBe(VaultErrorCode.AlreadyExists);

        _v.ReadText("Notes/b.md").ShouldBe("sync got here first\n", "their file is not ours to replace");
        _v.ReadText("Notes/a.md").ShouldBe("agent base\n", "the captured source must be put back");
        _v.TempFiles().ShouldBeEmpty();
    }

    /// <summary>
    /// The reversal of a previously-pinned unsafe outcome. Under the old
    /// content-token design, a replacement whose bytes matched ours was
    /// deleted on rollback — and if the external writer had also taken the
    /// source, that pathname was the last place the note existed. Byte
    /// equality is not ownership and is not continued existence; the
    /// replacement survives now because nothing publicly-named is deleted at
    /// all.
    /// </summary>
    [Fact]
    public void A_byte_identical_replacement_at_the_destination_survives()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        _v.Service.BeforeCommitTestHook = (source, destination) =>
        {
            File.WriteAllText(destination, "agent base\n"); // same bytes, different inode
            ExternalReplace(source, "and a new note at the old name\n");
        };

        Should.Throw<KnapperException>(() => _v.Service.Move("Notes/a.md", "Notes/b.md", sha));

        _v.ReadText("Notes/b.md").ShouldBe("agent base\n",
            "a pathname another writer created must survive even when its bytes match ours");
        _v.ReadText("Notes/a.md").ShouldBe("and a new note at the old name\n",
            "their replacement of the source survives too");
        _v.TempFiles().ShouldBeEmpty();
    }

    // ---- destination attacked AFTER the commit -----------------------------

    [Fact]
    public void A_destination_replaced_after_the_commit_is_left_alone_and_the_move_rolls_back()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        _v.Service.AfterCommitTestHook = (_, destination) => ExternalReplace(destination, "sync wrote here\n");

        Should.Throw<KnapperException>(() => _v.Service.Move("Notes/a.md", "Notes/b.md", sha))
            .Code.ShouldBe(VaultErrorCode.PreconditionFailed);

        _v.ReadText("Notes/a.md").ShouldBe("agent base\n", "the source comes back from the captured copy");
        _v.ReadText("Notes/b.md").ShouldBe("sync wrote here\n", "their file is not ours to remove");
        _v.TempFiles().ShouldBeEmpty();
    }

    /// <summary>
    /// Removal in the window between the commit link and the destination
    /// verification: the verification notices, the operation fails, and the
    /// source comes back from the captured copy. (This hook fires BEFORE the
    /// destination is verified — the post-verification window is the
    /// after-capture family below, on the far side of the commit boundary.)
    /// </summary>
    [Fact]
    public void A_destination_removed_between_commit_and_verification_keeps_the_content()
    {
        var sha = _v.Write("Notes/a.md", "the only copy\n");
        _v.Service.AfterCommitTestHook = (_, destination) => File.Delete(destination);

        Should.Throw<KnapperException>(() => _v.Service.Move("Notes/a.md", "Notes/b.md", sha))
            .Code.ShouldBe(VaultErrorCode.PreconditionFailed);

        _v.ReadText("Notes/a.md").ShouldBe("the only copy\n");
        File.Exists(_v.Absolute("Notes/b.md")).ShouldBeFalse();
        _v.TempFiles().ShouldBeEmpty();
    }

    // ---- the far side of the commit boundary: after capture -----------------

    /// <summary>
    /// The destination removed AFTER it was published, verified, and the
    /// source captured. The move is already committed at that point, so this
    /// is the external writer deleting the note — the operation reports
    /// SUCCESS and their delete stands, the same as a delete landing a
    /// millisecond after the move returned. The alternative — a second
    /// destination check deciding whether the hidden links may go — is
    /// check-then-delete over a pathname other writers own, the exact shape
    /// this design removed. The review that prompted this test flagged the
    /// window as uncovered; this pins the chosen (linearizable) semantics.
    /// </summary>
    [Fact]
    public void A_destination_removed_after_the_capture_is_the_external_writers_delete()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        _v.Service.AfterCaptureTestHook = (_, destination) => File.Delete(destination);

        var result = _v.Service.Move("Notes/a.md", "Notes/b.md", sha);

        result.Path.ShouldBe("Notes/b.md");
        File.Exists(_v.Absolute("Notes/a.md")).ShouldBeFalse("the move completed");
        File.Exists(_v.Absolute("Notes/b.md")).ShouldBeFalse("their post-commit delete stands");
        _v.TempFiles().ShouldBeEmpty("hidden links are cleanup, not a recovery journal");
    }

    [Fact]
    public void A_destination_replaced_after_the_capture_is_the_external_writers_overwrite()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        _v.Service.AfterCaptureTestHook = (_, destination) => ExternalReplace(destination, "sync wrote here\n");

        var result = _v.Service.Move("Notes/a.md", "Notes/b.md", sha);

        result.Path.ShouldBe("Notes/b.md");
        File.Exists(_v.Absolute("Notes/a.md")).ShouldBeFalse();
        _v.ReadText("Notes/b.md").ShouldBe("sync wrote here\n", "their post-commit overwrite stands");
        _v.TempFiles().ShouldBeEmpty();
    }

    [Fact]
    public void A_trash_entry_removed_after_the_capture_is_the_external_writers_delete()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        _v.Service.AfterCaptureTestHook = (_, trash) => File.Delete(trash);

        var result = _v.Service.Delete("Notes/a.md", sha);

        result.TrashPath.ShouldBe(".trash/Notes/a.md");
        File.Exists(_v.Absolute("Notes/a.md")).ShouldBeFalse("the delete completed");
        File.Exists(_v.Absolute(".trash/Notes/a.md")).ShouldBeFalse("their removal of the trash copy stands");
        _v.TempFiles().ShouldBeEmpty();
    }

    [Fact]
    public void A_trash_entry_replaced_after_the_capture_is_the_external_writers_overwrite()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        _v.Service.AfterCaptureTestHook = (_, trash) => ExternalReplace(trash, "someone else's trash\n");

        var result = _v.Service.Delete("Notes/a.md", sha);

        result.TrashPath.ShouldBe(".trash/Notes/a.md");
        File.Exists(_v.Absolute("Notes/a.md")).ShouldBeFalse();
        _v.ReadText(".trash/Notes/a.md").ShouldBe("someone else's trash\n",
            "their post-commit overwrite of the trash copy stands");
        _v.TempFiles().ShouldBeEmpty();
    }

    /// <summary>
    /// A remote rename delivered mid-move that takes BOTH pathnames, each
    /// with a new inode, the way Sync replaces a file. Knapper must remove
    /// nothing of theirs and must not report success.
    ///
    /// <para>The version the agent read survives nowhere afterwards, and that
    /// is correct: the external writer replaced both pathnames themselves,
    /// before Knapper had removed anything. Keeping a hidden copy of every
    /// note a remote edit overwrites mid-race would fill the vault with
    /// invisible files nobody asked for. Hidden links are kept only when
    /// Knapper's own capture is what took the content out of public view —
    /// see `SourceCaptureRaceTests`.</para>
    /// </summary>
    [Fact]
    public void A_move_racing_a_remote_rename_over_both_pathnames_removes_nothing_of_theirs()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        _v.Service.AfterCommitTestHook = (source, destination) =>
        {
            ExternalReplace(destination, "remote rename landed\n");
            ExternalReplace(source, "and a new note at the old name\n");
        };

        Should.Throw<KnapperException>(() => _v.Service.Move("Notes/a.md", "Notes/b.md", sha))
            .Code.ShouldBe(VaultErrorCode.PreconditionFailed);

        _v.ReadText("Notes/b.md").ShouldBe("remote rename landed\n");
        _v.ReadText("Notes/a.md").ShouldBe("and a new note at the old name\n");
        _v.TempFiles().ShouldBeEmpty();
    }

    // ---- the same windows, aimed at a delete's trash entry ------------------

    [Fact]
    public void A_trash_entry_taken_before_the_commit_is_refused_and_left_alone()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        _v.Service.BeforeCommitTestHook = (_, trash) =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(trash)!);
            File.WriteAllText(trash, "someone else's trash entry\n");
        };

        Should.Throw<KnapperException>(() => _v.Service.Delete("Notes/a.md", sha))
            .Code.ShouldBe(VaultErrorCode.AlreadyExists);

        _v.ReadText(".trash/Notes/a.md").ShouldBe("someone else's trash entry\n");
        _v.ReadText("Notes/a.md").ShouldBe("agent base\n");
        _v.TempFiles().ShouldBeEmpty();
    }

    [Fact]
    public void A_trash_entry_removed_after_the_commit_keeps_the_content()
    {
        var sha = _v.Write("Notes/a.md", "the only copy\n");
        _v.Service.AfterCommitTestHook = (_, trash) => File.Delete(trash);

        Should.Throw<KnapperException>(() => _v.Service.Delete("Notes/a.md", sha))
            .Code.ShouldBe(VaultErrorCode.PreconditionFailed);

        _v.ReadText("Notes/a.md").ShouldBe("the only copy\n");
        File.Exists(_v.Absolute(".trash/Notes/a.md")).ShouldBeFalse();
        _v.TempFiles().ShouldBeEmpty();
    }

    [Fact]
    public void A_trash_entry_replaced_after_the_commit_is_left_alone()
    {
        var sha = _v.Write("Notes/a.md", "agent base\n");
        _v.Service.AfterCommitTestHook = (_, trash) => ExternalReplace(trash, "not what we linked\n");

        Should.Throw<KnapperException>(() => _v.Service.Delete("Notes/a.md", sha))
            .Code.ShouldBe(VaultErrorCode.PreconditionFailed);

        _v.ReadText("Notes/a.md").ShouldBe("agent base\n");
        _v.ReadText(".trash/Notes/a.md").ShouldBe("not what we linked\n");
        _v.TempFiles().ShouldBeEmpty();
    }

    // ---- the unraced path is unchanged -------------------------------------

    [Fact]
    public void An_unraced_move_preserves_bytes_mode_and_the_no_clobber_rule()
    {
        var sha = _v.Write("Notes/a.md", "content a\n");
        File.SetUnixFileMode(_v.Absolute("Notes/a.md"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        var result = _v.Service.Move("Notes/a.md", "Notes/b.md", sha);

        result.Path.ShouldBe("Notes/b.md");
        _v.ReadText("Notes/b.md").ShouldBe("content a\n");
        File.Exists(_v.Absolute("Notes/a.md")).ShouldBeFalse();
        File.GetUnixFileMode(_v.Absolute("Notes/b.md"))
            .ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        _v.TempFiles().ShouldBeEmpty("a completed move keeps no temps");

        var shaB = VaultHash.Sha256Hex(System.Text.Encoding.UTF8.GetBytes("content a\n"));
        _v.Write("Notes/c.md", "content c\n");
        Should.Throw<KnapperException>(() => _v.Service.Move("Notes/b.md", "Notes/c.md", shaB))
            .Code.ShouldBe(VaultErrorCode.AlreadyExists);
        _v.ReadText("Notes/c.md").ShouldBe("content c\n");
        _v.ReadText("Notes/b.md").ShouldBe("content a\n");
    }

    [Fact]
    public void An_unraced_delete_leaves_no_temps()
    {
        var sha = _v.Write("Notes/a.md", "content a\n");
        var result = _v.Service.Delete("Notes/a.md", sha);

        result.TrashPath.ShouldBe(".trash/Notes/a.md");
        _v.ReadText(result.TrashPath).ShouldBe("content a\n");
        _v.TempFiles().ShouldBeEmpty();
    }
}
