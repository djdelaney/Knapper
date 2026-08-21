using Knapper.Core.Mutation;
using Knapper.Core.Vault;

namespace Knapper.Core.Tests.Mutation;

/// <summary>
/// The two conflict families and the shapes their detection must survive.
/// The `(Knapper displaced …)` sibling is published NO-FOLLOW, so it can be
/// a symlink — and the health walk used to skip every reparse point before
/// looking at the name, reporting a green board over a note the gate was
/// blocking (review round three). Recognition is by NAME and never follows
/// the entry; recursion still refuses directory symlinks, so the cycle that
/// once hung the oversized walk stays impossible.
/// </summary>
public sealed class ConflictMarkerTests : IDisposable
{
    private readonly TempDir _vault = new();
    private readonly TempDir _outside = new();
    private readonly VaultPathResolver _resolver;
    private readonly ConflictDetector _detector;

    public ConflictMarkerTests()
    {
        _resolver = new VaultPathResolver(_vault.Path);
        _detector = new ConflictDetector(_resolver);
    }

    public void Dispose()
    {
        _outside.Dispose();
        _vault.Dispose();
    }

    [Fact]
    public void A_displaced_sibling_blocks_the_original_like_a_sync_conflict()
    {
        _vault.File("Notes/a.md", "content\n");
        _vault.File("Notes/a (Knapper displaced 2026-08-20 12-00-00 abcd1234).md", "displaced\n");

        Should.Throw<KnapperException>(() => _detector.AssertNotConflicted(_resolver.Resolve("Notes/a.md")))
            .Code.ShouldBe(VaultErrorCode.MutationBlocked);
        Should.Throw<KnapperException>(() => _detector.AssertNotConflicted(
                _resolver.Resolve("Notes/a (Knapper displaced 2026-08-20 12-00-00 abcd1234).md")))
            .Code.ShouldBe(VaultErrorCode.MutationBlocked);
    }

    [Fact]
    public void The_scan_lists_a_symlink_shaped_conflict_entry_without_following_it()
    {
        _vault.File("Notes/a.md", "content\n");
        var outsideTarget = Path.Combine(_outside.Path, "gone.md");
        File.CreateSymbolicLink(
            Path.Combine(_vault.Path, "Notes/a (Knapper displaced 2026-08-20 12-00-00 abcd1234).md"),
            outsideTarget); // dangling, deliberately: recognition must not need the target

        var found = _detector.ScanAll();

        found.ShouldContain("Notes/a (Knapper displaced 2026-08-20 12-00-00 abcd1234).md",
            "a recovery object that happens to be a symlink must not vanish from health");
    }

    [Fact]
    public void The_scan_still_never_recurses_into_a_directory_symlink()
    {
        _vault.File("Real/a (Conflicted copy 2026-08-20).md", "conflict\n");
        Directory.CreateDirectory(Path.Combine(_outside.Path, "elsewhere"));
        File.WriteAllText(Path.Combine(_outside.Path, "elsewhere", "b (Conflicted copy 2026-08-20).md"), "x\n");
        Directory.CreateSymbolicLink(Path.Combine(_vault.Path, "Doorway"), Path.Combine(_outside.Path, "elsewhere"));

        var found = _detector.ScanAll();

        found.ShouldContain("Real/a (Conflicted copy 2026-08-20).md");
        found.ShouldNotContain("Doorway/b (Conflicted copy 2026-08-20).md",
            "content behind a directory symlink is outside the walk, always");
    }
}
