using Knapper.Core.Vault;

namespace Knapper.Core.Tests;

/// <summary>
/// The startup fail-closed checks stand on these answers: a lexical prefix
/// check misses (a) a lock/audit path EQUAL to the vault root and (b) a
/// path whose ancestor is a symlink back into the vault — both would put
/// operational files inside the synced tree.
/// </summary>
public sealed class PathContainmentTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Vault => Path.Combine(_dir.Path, "vault");

    public PathContainmentTests() => Directory.CreateDirectory(Vault);

    [Fact]
    public void The_vault_root_itself_counts_as_inside()
    {
        PathContainment.IsInsideOrEqual(Vault, Vault).ShouldBeTrue();
        PathContainment.IsInsideOrEqual(Vault + "/", Vault).ShouldBeTrue();
    }

    [Fact]
    public void Children_are_inside_siblings_and_prefix_lookalikes_are_not()
    {
        PathContainment.IsInsideOrEqual(Path.Combine(Vault, "locks"), Vault).ShouldBeTrue();
        PathContainment.IsInsideOrEqual(Path.Combine(_dir.Path, "outside"), Vault).ShouldBeFalse();
        // "/vault-locks" must not read as inside "/vault".
        PathContainment.IsInsideOrEqual(Vault + "-locks", Vault).ShouldBeFalse();
    }

    [Fact]
    public void A_symlinked_ancestor_pointing_into_the_vault_is_detected()
    {
        var link = Path.Combine(_dir.Path, "innocent-looking");
        File.CreateSymbolicLink(link, Vault);

        // Lexically outside the vault; physically inside it.
        PathContainment.IsInsideOrEqual(Path.Combine(link, "locks"), Vault).ShouldBeTrue();
        PathContainment.IsInsideOrEqual(link, Vault).ShouldBeTrue();
    }

    [Fact]
    public void A_not_yet_existing_tail_still_resolves_through_its_existing_ancestors()
    {
        var link = Path.Combine(_dir.Path, "link");
        File.CreateSymbolicLink(link, Vault);

        // Neither "locks" nor "sub" exists yet — the symlinked ancestor must
        // still be resolved (lock dirs and audit files are created AFTER the
        // startup check).
        PathContainment.IsInsideOrEqual(Path.Combine(link, "sub", "locks"), Vault).ShouldBeTrue();
        PathContainment.IsInsideOrEqual(Path.Combine(_dir.Path, "sub", "locks"), Vault).ShouldBeFalse();
    }

    [Fact]
    public void A_symlinked_vault_root_is_canonicalized_before_comparison()
    {
        var rootLink = Path.Combine(_dir.Path, "vault-link");
        File.CreateSymbolicLink(rootLink, Vault);

        PathContainment.IsInsideOrEqual(Path.Combine(Vault, "locks"), rootLink).ShouldBeTrue();
    }
}
