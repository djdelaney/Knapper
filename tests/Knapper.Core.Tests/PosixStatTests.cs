using System.Diagnostics;
using Knapper.Core.Interop;

namespace Knapper.Core.Tests;

/// <summary>
/// Layout pins for <see cref="Posix.LStat"/>. The struct offsets are
/// hand-transcribed per platform (statx on Linux, the 64-bit-inode stat on
/// macOS arm64), and a wrong offset would not crash — it would return
/// plausible garbage that the ownership and authorized-base judgements then
/// trust. Every field the judgements consume is cross-checked here against
/// an independent source (the BCL, or a known action's effect), on the
/// platform actually running the suite.
/// </summary>
public sealed class PosixStatTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void A_regular_file_reports_type_size_mode_and_a_sane_mtime()
    {
        var path = _dir.File("note.md", "twelve bytes");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        var stat = Posix.LStat(path);

        stat.IsRegular.ShouldBeTrue();
        stat.IsSymlink.ShouldBeFalse();
        stat.Size.ShouldBe(new FileInfo(path).Length);
        stat.Permissions.ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var mtime = DateTimeOffset.FromUnixTimeSeconds(stat.MtimeSec).UtcDateTime;
        (File.GetLastWriteTimeUtc(path) - mtime).Duration()
            .ShouldBeLessThan(TimeSpan.FromSeconds(2), "a wrong mtime offset reads garbage seconds");
    }

    [Fact]
    public void Identity_is_stable_across_calls_and_shared_by_hard_links_only()
    {
        var a = _dir.File("a.md", "content");
        var b = _dir.File("b.md", "content"); // same bytes, different inode
        var linked = Path.Combine(_dir.Path, "linked.md");
        Posix.LinkNoFollow(a, linked);

        Posix.LStat(a).SameIdentity(Posix.LStat(a)).ShouldBeTrue();
        Posix.LStat(a).SameIdentity(Posix.LStat(linked)).ShouldBeTrue("hard links are the same file");
        Posix.LStat(a).SameIdentity(Posix.LStat(b)).ShouldBeFalse("byte equality is not identity");
    }

    /// <summary>
    /// LAYOUT evidence only: size and mtime move on an ordinary in-place
    /// write, so the transcribed offsets are reading the real fields. The
    /// INVERSE is false and load-bearing: an equal size+mtime does NOT prove
    /// unchanged bytes (mtime is user-settable and granularity-aliased),
    /// which is why no judgement may use these fields as a content
    /// precondition — round four reproduced silent loss through exactly that
    /// inference, pinned at the scenario level by ReplaceCommitRaceTests.
    /// </summary>
    [Fact]
    public void Size_and_mtime_move_on_an_in_place_write_but_are_never_a_content_proof()
    {
        var path = _dir.File("note.md", "before");
        var first = Posix.LStat(path);

        Thread.Sleep(20); // outrun coarse filesystem clocks
        using (var stream = new FileStream(path, FileMode.Append))
            stream.Write(" and after"u8);

        var second = Posix.LStat(path);
        second.SameIdentity(first).ShouldBeTrue("an in-place write keeps the inode");
        second.Size.ShouldNotBe(first.Size, "the size offset must read a field the append moved");
        (second.MtimeSec != first.MtimeSec || second.MtimeNsec != first.MtimeNsec)
            .ShouldBeTrue("the mtime offsets must read fields the write moved");
    }

    [Fact]
    public void A_symlink_is_the_entry_itself_never_the_target()
    {
        var target = _dir.File("target.md", "content");
        var link = Path.Combine(_dir.Path, "link.md");
        File.CreateSymbolicLink(link, target);
        var dangling = Path.Combine(_dir.Path, "dangling.md");
        File.CreateSymbolicLink(dangling, Path.Combine(_dir.Path, "nowhere.md"));

        Posix.LStat(link).IsSymlink.ShouldBeTrue();
        Posix.LStat(link).IsRegular.ShouldBeFalse();
        Posix.LStat(dangling).IsSymlink.ShouldBeTrue("a dangling symlink is still a symlink");
        Posix.LStat(link).SameIdentity(Posix.LStat(target))
            .ShouldBeFalse("the link's own inode, not the target's");
    }

    [Fact]
    public void A_fifo_is_neither_regular_nor_a_symlink()
    {
        var fifo = Path.Combine(_dir.Path, "pipe");
        var psi = new ProcessStartInfo("mkfifo");
        psi.ArgumentList.Add(fifo);
        using (var p = Process.Start(psi)!)
        {
            p.WaitForExit();
            p.ExitCode.ShouldBe(0);
        }

        var stat = Posix.LStat(fifo);
        stat.IsRegular.ShouldBeFalse();
        stat.IsSymlink.ShouldBeFalse();
    }

    [Fact]
    public void A_missing_path_is_NotFound()
    {
        Should.Throw<KnapperException>(() => Posix.LStat(Path.Combine(_dir.Path, "ghost")))
            .Code.ShouldBe(VaultErrorCode.NotFound);
    }
}
