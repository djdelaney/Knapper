using Knapper.Core.Mutation;
using Knapper.Core.Vault;

namespace Knapper.Core.Tests.Mutation;

/// <summary>
/// The conflict walk runs UNCACHED on every /health and /up request — the
/// host monitor polls every 5 minutes — and it is the walk whose finding
/// decides between a 503 and a green board. It had the reparse-point filter
/// (so no symlink cycle can spin it) but no wall clock, which left the
/// endpoint that exists to notice trouble able to sit on a slow tree
/// indefinitely.
///
/// <para>Its budget answers the same way <c>OversizedFiles.Scan</c>'s does:
/// by throwing, so "could not tell" can never reach a caller wearing the
/// shape of "scanned, none found".</para>
/// </summary>
public sealed class ConflictScanBudgetTests
{
    private static ConflictDetector Detector(TempDir vault) =>
        new(new VaultPathResolver(vault.Path));

    [Fact]
    public void A_bounded_scan_finds_the_conflict_files_and_nothing_else()
    {
        using var vault = new TempDir();
        vault.File("Notes/Daily.md", "note\n");
        vault.File("Notes/Daily (Conflicted copy 2026-08-19).md", "sync's copy\n");

        Detector(vault).ScanAll(TimeSpan.FromSeconds(5))
            .ShouldBe(["Notes/Daily (Conflicted copy 2026-08-19).md"]);
    }

    /// <summary>
    /// Throws rather than returning the short list. A partial conflict scan
    /// reported as "none" is how a vault with an unreconciled conflict file
    /// looks healthy — the one lie this walk exists to prevent.
    /// </summary>
    [Fact]
    public void An_expired_budget_throws_rather_than_reporting_a_partial_walk()
    {
        using var vault = new TempDir();
        vault.File("Notes/Daily (Conflicted copy 2026-08-19).md", "sync's copy\n");

        Should.Throw<TimeoutException>(() => Detector(vault).ScanAll(TimeSpan.Zero))
            .Message.ShouldContain("budget");
    }

    /// <summary>
    /// The same contract as every other vault walk: symlinks are skipped, not
    /// followed. Asserting termination is the point — the budget is only the
    /// backstop that turns a regression into a 5s failure rather than a hung
    /// test process.
    /// </summary>
    [Fact]
    public void A_directory_symlink_cycle_neither_hangs_the_walk_nor_appears_in_it()
    {
        using var vault = new TempDir();
        vault.File("Notes/Daily (Conflicted copy 2026-08-19).md", "sync's copy\n");
        Directory.CreateSymbolicLink(Path.Combine(vault.Path, "Notes", "loop"), vault.Path);

        Detector(vault).ScanAll(TimeSpan.FromSeconds(5))
            .ShouldBe(["Notes/Daily (Conflicted copy 2026-08-19).md"]);
    }

    [Fact]
    public void Dot_directories_are_skipped_at_every_depth()
    {
        using var vault = new TempDir();
        vault.File(".trash/Notes/Daily (Conflicted copy 2026-08-19).md", "already dealt with\n");

        Detector(vault).ScanAll(TimeSpan.FromSeconds(5)).ShouldBeEmpty();
    }
}
