using Knapper.Core.Vault;

namespace Knapper.Core.Tests.Mutation;

/// <summary>
/// The window between publishing the destination and capturing the source.
/// Two ways it went wrong before, both reported by review and both
/// reproduced here first:
///
/// <list type="number">
/// <item>the destination's directory replaced by a symlink AFTER containment
/// was proved, so the commit and its verification both succeeded through the
/// symlink and a delete reported success with the note outside the vault;</item>
/// <item><c>File.ReadAllBytes</c> answering
/// <c>UnauthorizedAccessException</c> — which is NOT an
/// <c>IOException</c> — when the destination has become a directory or an
/// unreadable file, escaping a handler that caught only
/// <c>KnapperException or IOException</c> and taking the last links to the
/// original with it on the way out.</item>
/// </list>
///
/// <para>Both are now failures with the source still at its own pathname,
/// because the capture happens last: at this point in the operation Knapper
/// has removed nothing, so there is nothing to unwind.</para>
/// </summary>
public sealed class PostCommitFailureTests : IDisposable
{
    private readonly MutationVault _v = new();
    private readonly TempDir _outside = new();

    public void Dispose()
    {
        _outside.Dispose();
        _v.Dispose();
    }

    private string[] EscapedFiles() =>
        Directory.EnumerateFiles(_outside.Path, "*", SearchOption.AllDirectories).ToArray();

    // ---- the directory swapped after containment was proved ----------------

    /// <summary>
    /// The reviewer's construction: move the destination's parent — private
    /// temp and all — to a directory outside the vault on the same
    /// filesystem, then put a symlink at its old name. Every subsequent
    /// path-based operation resolves through the symlink.
    /// </summary>
    private void SwapDestinationDirectoryOutside(string destination)
    {
        var stolen = Path.Combine(_outside.Path, "stolen");
        var directory = Path.GetDirectoryName(destination)!;
        Directory.Move(directory, stolen);
        Directory.CreateSymbolicLink(directory, stolen);
    }

    [Fact]
    public void A_delete_whose_trash_directory_is_swapped_after_containment_does_not_report_success()
    {
        var sha = _v.Write("Notes/a.md", "private\n");
        _v.Service.BeforeCommitTestHook = (_, trash) => SwapDestinationDirectoryOutside(trash);

        Should.Throw<KnapperException>(() => _v.Service.Delete("Notes/a.md", sha))
            .Code.ShouldBe(VaultErrorCode.PathOutsideVault);

        _v.ReadText("Notes/a.md").ShouldBe("private\n",
            "the source is still untouched at this point — the capture comes after");
    }

    [Fact]
    public void A_move_whose_destination_directory_is_swapped_after_containment_does_not_report_success()
    {
        Directory.CreateDirectory(_v.Absolute("Archive"));
        var sha = _v.Write("Notes/a.md", "private\n");
        _v.Service.BeforeCommitTestHook = (_, destination) => SwapDestinationDirectoryOutside(destination);

        Should.Throw<KnapperException>(() => _v.Service.Move("Notes/a.md", "Archive/a.md", sha))
            .Code.ShouldBe(VaultErrorCode.PathOutsideVault);

        _v.ReadText("Notes/a.md").ShouldBe("private\n");
    }

    /// <summary>
    /// What the containment check does NOT do, stated as a test so it is a
    /// known quantity rather than a claim: the escaped link is not removed.
    ///
    /// <para>Deleting it would mean unlinking a pathname on the strength of
    /// having created it a syscall earlier — the exact shape that destroyed
    /// an external writer's file twice in this codebase's history. The
    /// content is not at risk (the source never moved), and an actor able to
    /// move a vault directory can already read the vault, so leaving the link
    /// discloses nothing they could not already take. Descriptor-relative
    /// linkat would not prevent this either: a directory MOVED out of the
    /// vault is followed by any handle to it.</para>
    /// </summary>
    [Fact]
    public void The_escaped_link_is_reported_rather_than_deleted()
    {
        var sha = _v.Write("Notes/a.md", "private\n");
        _v.Service.BeforeCommitTestHook = (_, trash) => SwapDestinationDirectoryOutside(trash);

        var ex = Should.Throw<KnapperException>(() => _v.Service.Delete("Notes/a.md", sha));
        ex.Message.ShouldContain("outside the vault");

        _v.ReadText("Notes/a.md").ShouldBe("private\n");
        EscapedFiles().Length.ShouldBe(1, "the escaped link is left where it is, and named in the error");
        File.ReadAllText(EscapedFiles()[0]).ShouldBe("private\n");
    }

    // ---- the destination becomes unreadable, or stops being a file ---------

    [Fact]
    public void A_destination_that_becomes_a_directory_fails_without_losing_the_original()
    {
        var sha = _v.Write("Notes/a.md", "the only copy\n");
        _v.Service.AfterCommitTestHook = (_, destination) =>
        {
            File.Delete(destination);
            Directory.CreateDirectory(destination); // ReadAllBytes → UnauthorizedAccessException
        };

        Should.Throw<KnapperException>(() => _v.Service.Move("Notes/a.md", "Notes/b.md", sha))
            .Code.ShouldBe(VaultErrorCode.PreconditionFailed);

        _v.ReadText("Notes/a.md").ShouldBe("the only copy\n",
            "an exception type the handler did not list must not cost the note");
        Directory.Exists(_v.Absolute("Notes/b.md")).ShouldBeTrue("their directory is not ours to remove");
        _v.TempFiles().ShouldBeEmpty();
    }

    [Fact]
    public void A_delete_whose_trash_entry_becomes_a_directory_fails_without_losing_the_original()
    {
        var sha = _v.Write("Notes/a.md", "the only copy\n");
        _v.Service.AfterCommitTestHook = (_, trash) =>
        {
            File.Delete(trash);
            Directory.CreateDirectory(trash);
        };

        Should.Throw<KnapperException>(() => _v.Service.Delete("Notes/a.md", sha))
            .Code.ShouldBe(VaultErrorCode.PreconditionFailed);

        _v.ReadText("Notes/a.md").ShouldBe("the only copy\n");
        _v.TempFiles().ShouldBeEmpty();
    }

    [Fact]
    public void A_destination_that_becomes_unreadable_fails_without_losing_the_original()
    {
        var sha = _v.Write("Notes/a.md", "the only copy\n");
        _v.Service.AfterCommitTestHook = (_, destination) =>
        {
            var foreign = destination + ".foreign";
            File.WriteAllText(foreign, "someone else's file\n");
            File.SetUnixFileMode(foreign, UnixFileMode.None);
            File.Move(foreign, destination, overwrite: true);
        };

        try
        {
            Should.Throw<KnapperException>(() => _v.Service.Move("Notes/a.md", "Notes/b.md", sha))
                .Code.ShouldBe(VaultErrorCode.PreconditionFailed);

            _v.ReadText("Notes/a.md").ShouldBe("the only copy\n");
            File.GetUnixFileMode(_v.Absolute("Notes/b.md")).ShouldBe(UnixFileMode.None,
                "their unreadable file survives untouched");
            _v.TempFiles().ShouldBeEmpty();
        }
        finally
        {
            var stray = _v.Absolute("Notes/b.md");
            if (File.Exists(stray))
                File.SetUnixFileMode(stray, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
