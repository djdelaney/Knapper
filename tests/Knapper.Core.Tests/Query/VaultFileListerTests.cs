using System.Diagnostics;
using Knapper.Core.Query;

namespace Knapper.Core.Tests.Query;

public sealed class VaultFileListerTests : IClassFixture<FixtureVault>
{
    private readonly FixtureVault _vault;

    public VaultFileListerTests(FixtureVault vault) => _vault = vault;

    [Fact]
    public void Lists_every_visible_file_in_ordinal_order_and_nothing_hidden()
    {
        var result = _vault.Lister.List(new VaultFilesQuery { Kind = EntryKind.File });
        result.Items.Select(i => i.Path).ShouldBe(FixtureVault.VisibleFiles);
        result.Truncated.ShouldBeFalse();
        result.TotalMatches.ShouldBe(FixtureVault.VisibleFiles.Length);
    }

    [Fact]
    public void Agrees_with_ripgrep_about_what_exists()
    {
        // The lister is native; searches go through rg. This differential
        // check is what keeps the two implementations from ever disagreeing
        // about which files are visible.
        var psi = new ProcessStartInfo
        {
            FileName = "rg",
            WorkingDirectory = _vault.Resolver.Root,
            RedirectStandardOutput = true,
        };
        foreach (var a in (string[])["--files", "--no-config", "--no-ignore", "--no-follow", "--sort=path"])
            psi.ArgumentList.Add(a);
        using var rg = Process.Start(psi)!;
        var rgFiles = rg.StandardOutput.ReadToEnd()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Order(StringComparer.Ordinal)
            .ToArray();
        rg.WaitForExit();

        var ours = _vault.Lister.List(new VaultFilesQuery { Kind = EntryKind.File })
            .Items.Select(i => i.Path).Order(StringComparer.Ordinal).ToArray();
        ours.ShouldBe(rgFiles);
    }

    [Fact]
    public void Directories_are_listed_when_asked()
    {
        var result = _vault.Lister.List(new VaultFilesQuery { Kind = EntryKind.Directory });
        result.Items.ShouldAllBe(i => i.IsDirectory && i.Size == null);
        result.Items.Select(i => i.Path).ShouldContain("Notes/Sub");
        result.Items.Select(i => i.Path).ShouldNotContain(".git");
    }

    [Fact]
    public void Glob_and_extension_and_prefix_filters_work()
    {
        _vault.Lister.List(new VaultFilesQuery { Glob = "*.sh" })
            .Items.Select(i => i.Path).ShouldBe(["scripts/backup.sh"]);

        _vault.Lister.List(new VaultFilesQuery { Glob = "many/needles-[01].md" })
            .Items.Select(i => i.Path).ShouldBe(["many/needles-0.md", "many/needles-1.md"]);

        _vault.Lister.List(new VaultFilesQuery { Extensions = [".bin"] })
            .Items.Select(i => i.Path).ShouldBe(["raw/blob.bin"]);

        _vault.Lister.List(new VaultFilesQuery { PathPrefix = "Notes", Kind = EntryKind.File })
            .Items.Select(i => i.Path).ShouldBe(["Notes/Daily.md", "Notes/Sub/Deep.md"]);
    }

    [Fact]
    public void Size_and_mtime_filters_work()
    {
        _vault.Lister.List(new VaultFilesQuery { MinSize = 1 })
            .Items.Select(i => i.Path).ShouldNotContain("empty.md");

        var future = DateTimeOffset.UtcNow.AddHours(1);
        _vault.Lister.List(new VaultFilesQuery { MtimeAfter = future, Kind = EntryKind.File })
            .Items.ShouldBeEmpty();
        _vault.Lister.List(new VaultFilesQuery { MtimeBefore = future, Kind = EntryKind.File })
            .Items.Count.ShouldBe(FixtureVault.VisibleFiles.Length);
    }

    [Fact]
    public void Pagination_recombines_exactly()
    {
        var query = new VaultFilesQuery { Kind = EntryKind.File, MaxResults = 5 };
        var all = new List<string>();
        string? cursor = null;
        while (true)
        {
            var page = _vault.Lister.List(query with { Cursor = cursor });
            all.AddRange(page.Items.Select(i => i.Path));
            page.TotalMatches.ShouldBe(FixtureVault.VisibleFiles.Length); // known on every page: the walk completes
            if (!page.Truncated)
                break;
            cursor = page.NextCursor.ShouldNotBeNull();
        }
        all.ShouldBe(FixtureVault.VisibleFiles);
    }

    [Fact]
    public void Sha_metadata_is_optional_and_correct()
    {
        var withSha = _vault.Lister.List(new VaultFilesQuery { Glob = "empty.md", IncludeSha = true });
        withSha.Items.ShouldHaveSingleItem().Sha256
            .ShouldBe("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"); // sha256 of empty
        _vault.Lister.List(new VaultFilesQuery { Glob = "empty.md" })
            .Items.ShouldHaveSingleItem().Sha256.ShouldBeNull();
    }

    [Fact]
    public void Missing_prefix_is_NotFound_and_banned_prefix_is_BannedPath()
    {
        Should.Throw<KnapperException>(() => _vault.Lister.List(new VaultFilesQuery { PathPrefix = "nope" }))
            .Code.ShouldBe(VaultErrorCode.NotFound);
        Should.Throw<KnapperException>(() => _vault.Lister.List(new VaultFilesQuery { PathPrefix = ".git" }))
            .Code.ShouldBe(VaultErrorCode.BannedPath);
    }

    [Fact]
    public void Symlinks_are_not_listed()
    {
        using var other = new TempDir();
        other.File("outside.md", "outside");
        var link = Path.Combine(_vault.Dir.Path, "linked.md");
        File.CreateSymbolicLink(link, Path.Combine(other.Path, "outside.md"));
        try
        {
            _vault.Lister.List(new VaultFilesQuery { Kind = EntryKind.File })
                .Items.Select(i => i.Path).ShouldNotContain("linked.md");
        }
        finally
        {
            File.Delete(link);
        }
    }
}
