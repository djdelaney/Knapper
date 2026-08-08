using System.Text;
using Knapper.Core.Vault;

namespace Knapper.Core.Tests;

public sealed class AtomicFileTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Replace_swaps_content_and_preserves_mode()
    {
        var path = _dir.File("note.md", "old content");
        var restrictive = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        File.SetUnixFileMode(path, restrictive);

        AtomicFile.Replace(path, Bytes("new content"), VaultHash.Sha256Hex(Bytes("old content")));

        File.ReadAllText(path).ShouldBe("new content");
        File.GetUnixFileMode(path).ShouldBe(restrictive);
    }

    [Fact]
    public void Replace_rejects_stale_sha_and_mutates_nothing()
    {
        var path = _dir.File("note.md", "current content");

        var ex = Should.Throw<KnapperException>(() =>
            AtomicFile.Replace(path, Bytes("attacker wins"), VaultHash.Sha256Hex(Bytes("stale content"))));

        ex.Code.ShouldBe(VaultErrorCode.PreconditionFailed);
        File.ReadAllText(path).ShouldBe("current content");
    }

    [Fact]
    public void Replace_accepts_uppercase_sha()
    {
        var path = _dir.File("note.md", "old");
        AtomicFile.Replace(path, Bytes("new"), VaultHash.Sha256Hex(Bytes("old")).ToUpperInvariant());
        File.ReadAllText(path).ShouldBe("new");
    }

    [Fact]
    public void Replace_on_missing_file_is_NotFound()
    {
        Should.Throw<KnapperException>(() =>
                AtomicFile.Replace(Path.Combine(_dir.Path, "ghost.md"), Bytes("x"), VaultHash.Sha256Hex(Bytes("x"))))
            .Code.ShouldBe(VaultErrorCode.NotFound);
    }

    [Fact]
    public void No_temp_files_survive_any_outcome()
    {
        var path = _dir.File("note.md", "content");
        AtomicFile.Replace(path, Bytes("changed"), VaultHash.Sha256Hex(Bytes("content")));
        Should.Throw<KnapperException>(() =>
            AtomicFile.Replace(path, Bytes("nope"), VaultHash.Sha256Hex(Bytes("wrong"))));
        Should.Throw<KnapperException>(() =>
            AtomicFile.CreateNew(path, Bytes("nope")));

        Directory.EnumerateFiles(_dir.Path, AtomicFile.TempPrefix + "*", SearchOption.AllDirectories)
            .ShouldBeEmpty();
    }

    [Fact]
    public void CreateNew_creates_a_fresh_file()
    {
        var path = Path.Combine(_dir.Path, "fresh.md");
        AtomicFile.CreateNew(path, Bytes("hello"));
        File.ReadAllText(path).ShouldBe("hello");
    }

    [Fact]
    public void CreateNew_refuses_to_clobber_an_existing_file()
    {
        var path = _dir.File("existing.md", "precious");

        Should.Throw<KnapperException>(() => AtomicFile.CreateNew(path, Bytes("overwrite")))
            .Code.ShouldBe(VaultErrorCode.AlreadyExists);
        File.ReadAllText(path).ShouldBe("precious");
    }

    [Fact]
    public void CreateNew_refuses_a_missing_parent_directory()
    {
        Should.Throw<KnapperException>(() =>
                AtomicFile.CreateNew(Path.Combine(_dir.Path, "no-such-dir", "note.md"), Bytes("x")))
            .Code.ShouldBe(VaultErrorCode.NotFound);
    }

    [Fact]
    public void CreateNew_refuses_to_replace_a_dangling_symlink()
    {
        // A dangling symlink slips past the resolver's walk (Exists follows
        // links), so the create path itself must refuse it: link(2) fails
        // EEXIST on the link entry regardless of its target.
        var linkPath = Path.Combine(_dir.Path, "dangling.md");
        File.CreateSymbolicLink(linkPath, Path.Combine(_dir.Path, "nowhere.md"));

        Should.Throw<KnapperException>(() => AtomicFile.CreateNew(linkPath, Bytes("x")))
            .Code.ShouldBe(VaultErrorCode.AlreadyExists);
    }

    [Fact]
    public void VerifyOnDisk_passes_on_identical_bytes_and_fails_on_divergence()
    {
        var path = _dir.File("note.md", "written bytes");

        AtomicFile.VerifyOnDisk(path, Bytes("written bytes"));

        File.WriteAllText(path, "corrupted");
        Should.Throw<KnapperException>(() => AtomicFile.VerifyOnDisk(path, Bytes("written bytes")))
            .Code.ShouldBe(VaultErrorCode.VerifyFailed);
    }
}
