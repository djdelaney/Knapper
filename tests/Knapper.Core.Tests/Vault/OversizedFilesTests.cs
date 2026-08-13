using Knapper.Core.Vault;

namespace Knapper.Core.Tests.Vault;

/// <summary>
/// The scanner behind BOTH oversized backstops (/health and `knapper doctor`
/// share it so they cannot drift into disagreeing about whether the vault is
/// clean). Its failure mode is not a wrong answer, it is a confident one:
/// every caller treats what it returns as an exhaustively-walked vault.
/// </summary>
public sealed class OversizedFilesTests
{
    private const long Limit = 1000;

    private static void Big(TempDir vault, string relative) =>
        vault.File(relative, new string('x', 1500));

    [Fact]
    public void Reports_files_over_the_limit_and_nothing_else()
    {
        using var vault = new TempDir();
        Big(vault, "Big/stranded.md");
        vault.File("Notes/small.md", "small\n");

        OversizedFiles.Scan(vault.Path, Limit).ShouldBe(["Big/stranded.md"]);
    }

    /// <summary>
    /// This walk was the ONE vault walk that did not filter symlinks —
    /// `VaultFileLister` and `ConflictDetector` filter them, the resolver and
    /// the lock manager reject them outright. A directory-symlink CYCLE
    /// therefore made it non-terminating, on the request path of /health and
    /// /up: from runbook §8 the host monitor polls /up every 5 minutes, so a
    /// hung walk becomes a hung health endpoint on a fixed cadence, and the
    /// monitor's own alerting is what goes blind.
    ///
    /// The fixture is not hypothetical to this repo — `VaultPathResolverTests`
    /// already builds a directory symlink pointing at the vault root.
    /// </summary>
    [Fact]
    public void A_directory_symlink_cycle_neither_hangs_the_walk_nor_appears_in_it()
    {
        using var vault = new TempDir();
        Big(vault, "Notes/stranded.md");
        Directory.CreateSymbolicLink(Path.Combine(vault.Path, "Notes", "loop"), vault.Path);

        // The assertion IS termination; the budget is the backstop that would
        // turn a regression into a 5s failure instead of a hung test process.
        var found = OversizedFiles.Scan(vault.Path, Limit, TimeSpan.FromSeconds(5));

        found.ShouldBe(["Notes/stranded.md"]); // once — not once per cycle
    }

    /// <summary>
    /// A file symlink is skipped too, not just followed-but-deduped: an
    /// oversized file reported under a second, symlinked name would send a
    /// human looking for a path Knapper itself refuses to resolve.
    /// </summary>
    [Fact]
    public void A_symlink_to_an_oversized_file_is_not_reported()
    {
        using var vault = new TempDir();
        var target = vault.File("Big/stranded.md", new string('x', 1500));
        File.CreateSymbolicLink(Path.Combine(vault.Path, "alias.md"), target);

        OversizedFiles.Scan(vault.Path, Limit).ShouldBe(["Big/stranded.md"]);
    }

    /// <summary>
    /// An expiring budget THROWS. Returning what it had so far would be a
    /// partial walk reported as an exhaustive one — the same lie as an empty
    /// search claiming full coverage, and the exact failure the caller-side
    /// "unknown" state exists to carry.
    /// </summary>
    [Fact]
    public void An_expired_budget_throws_rather_than_returning_a_partial_walk()
    {
        using var vault = new TempDir();
        Big(vault, "Big/stranded.md");

        Should.Throw<TimeoutException>(() => OversizedFiles.Scan(vault.Path, Limit, TimeSpan.Zero));
    }

    /// <summary>
    /// Dot-entries are skipped at every depth, matching what queries can see.
    /// `.trash/` is the case worth naming: `vault_delete` is SOFT, so a file
    /// that arrived over-ceiling from a Mac and was then deleted still sits
    /// there. Reporting it would be a permanent alert about a file the human
    /// has already dealt with and cannot clear through any tool — there is no
    /// `vault_rmdir`, and `.trash/` is never swept.
    /// </summary>
    [Fact]
    public void Dot_directories_including_trash_are_not_walked()
    {
        using var vault = new TempDir();
        Big(vault, ".trash/Archive/deleted-but-oversized.md");
        Big(vault, ".git/objects/pack/big.pack");
        Big(vault, ".obsidian/plugins/omnisearch/main.js");

        OversizedFiles.Scan(vault.Path, Limit).ShouldBeEmpty();
    }

    /// <summary>
    /// A directory it cannot read is "could not tell", never "none found".
    /// Non-root: mode 000 is what makes the walk throw.
    /// </summary>
    [Fact]
    public void An_unreadable_directory_throws_rather_than_reporting_clean()
    {
        using var vault = new TempDir();
        var blocked = Path.Combine(vault.Path, "Locked");
        Directory.CreateDirectory(blocked);
        File.SetUnixFileMode(blocked, UnixFileMode.None);
        try
        {
            Should.Throw<UnauthorizedAccessException>(() => OversizedFiles.Scan(vault.Path, Limit));
        }
        finally
        {
            File.SetUnixFileMode(blocked,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
