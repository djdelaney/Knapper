using Knapper.Core.Vault;

namespace Knapper.Core.Tests;

public sealed class VaultPathResolverTests : IDisposable
{
    private readonly TempDir _vault = new();
    private readonly VaultPathResolver _resolver;

    public VaultPathResolverTests() => _resolver = new VaultPathResolver(_vault.Path);

    public void Dispose() => _vault.Dispose();

    [Theory]
    [InlineData("Notes/Daily.md")]
    [InlineData("with spaces/nöte – ünïcode.md")]
    [InlineData("a/b/c/d/e.md")]
    [InlineData("scripts/backup.sh")]
    public void Accepts_ordinary_vault_paths(string path)
    {
        var resolved = _resolver.Resolve(path);
        resolved.Relative.ShouldBe(path);
        resolved.Absolute.ShouldStartWith(_resolver.Root + "/");
    }

    [Fact]
    public void Normalizes_dot_segments_empty_segments_and_trailing_slash()
    {
        _resolver.Resolve("./Notes//Daily.md/").Relative.ShouldBe("Notes/Daily.md");
    }

    [Theory]
    [InlineData("", VaultErrorCode.InvalidPath)]
    [InlineData("   ", VaultErrorCode.InvalidPath)]
    [InlineData("/etc/passwd", VaultErrorCode.InvalidPath)]
    [InlineData("../outside.md", VaultErrorCode.InvalidPath)]
    [InlineData("Notes/../../outside.md", VaultErrorCode.InvalidPath)]
    [InlineData("Notes/..", VaultErrorCode.InvalidPath)]
    [InlineData("a\\b.md", VaultErrorCode.InvalidPath)]
    [InlineData("~/secrets.md", VaultErrorCode.InvalidPath)]
    [InlineData(".", VaultErrorCode.InvalidPath)]
    [InlineData(".git/config", VaultErrorCode.BannedPath)]
    [InlineData(".obsidian/app.json", VaultErrorCode.BannedPath)]
    [InlineData(".trash/deleted.md", VaultErrorCode.BannedPath)]
    [InlineData("Notes/.git/config", VaultErrorCode.BannedPath)]
    [InlineData(".knapper-tmp-abc123", VaultErrorCode.BannedPath)]
    [InlineData("Notes/.knapper-tmp-abc123", VaultErrorCode.BannedPath)]
    public void Rejects_hostile_or_banned_paths(string path, VaultErrorCode expected)
    {
        var ex = Should.Throw<KnapperException>(() => _resolver.Resolve(path));
        ex.Code.ShouldBe(expected);
    }

    [Fact]
    public void Rejects_NUL_in_path()
    {
        Should.Throw<KnapperException>(() => _resolver.Resolve("a\0b.md"))
            .Code.ShouldBe(VaultErrorCode.InvalidPath);
    }

    [Fact]
    public void Nonexistent_tail_resolves_fine_for_create()
    {
        _resolver.Resolve("brand/new/note.md").Relative.ShouldBe("brand/new/note.md");
    }

    [Fact]
    public void Rejects_symlink_file_inside_vault()
    {
        _vault.File("real.md", "content");
        File.CreateSymbolicLink(Path.Combine(_vault.Path, "link.md"), Path.Combine(_vault.Path, "real.md"));

        Should.Throw<KnapperException>(() => _resolver.Resolve("link.md"))
            .Code.ShouldBe(VaultErrorCode.SymlinkRejected);
    }

    [Fact]
    public void Rejects_symlink_directory_component_escaping_the_vault()
    {
        using var outside = new TempDir();
        outside.File("target/secret.md", "secret");
        Directory.CreateSymbolicLink(Path.Combine(_vault.Path, "escape"), Path.Combine(outside.Path, "target"));

        Should.Throw<KnapperException>(() => _resolver.Resolve("escape/secret.md"))
            .Code.ShouldBe(VaultErrorCode.SymlinkRejected);
    }

    [Fact]
    public void Rejects_symlink_directory_component_even_when_target_is_inside_the_vault()
    {
        _vault.File("real/note.md", "content");
        Directory.CreateSymbolicLink(Path.Combine(_vault.Path, "alias"), Path.Combine(_vault.Path, "real"));

        Should.Throw<KnapperException>(() => _resolver.Resolve("alias/note.md"))
            .Code.ShouldBe(VaultErrorCode.SymlinkRejected);
    }

    [Fact]
    public void Root_is_canonical_even_when_configured_through_a_symlink()
    {
        var link = Path.Combine(Path.GetTempPath(), "knapper-root-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateSymbolicLink(link, _vault.Path);
        try
        {
            var viaLink = new VaultPathResolver(link);
            viaLink.Root.ShouldBe(_resolver.Root);
        }
        finally
        {
            Directory.Delete(link);
        }
    }
}
