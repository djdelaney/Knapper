namespace Knapper.Mcp.Tests;

/// <summary>
/// The fail-closed boot checks (misconfiguration refuses startup, never
/// surfaces on the first tool call) must catch the containment bypasses a
/// lexical prefix check misses: lock/audit paths EQUAL to the vault root,
/// and paths reaching the vault through a symlinked ancestor.
/// </summary>
public class StartupContainmentTests
{
    /// <summary>
    /// The factory reads the settings dictionary lazily at build time, so
    /// entries referencing the factory's own directories can be added after
    /// construction and before CreateClient triggers the host build.
    /// </summary>
    private static void ShouldRefuseStartup(
        Func<KnapperMcpFactory, Dictionary<string, string?>> configure, string messagePart)
    {
        var settings = new Dictionary<string, string?>();
        using var factory = new KnapperMcpFactory(settings);
        foreach (var (key, value) in configure(factory))
            settings[key] = value;
        var ex = Should.Throw<Exception>(() => factory.CreateClient());
        (ex.Message + ex.InnerException?.Message).ShouldContain(messagePart);
    }

    [Fact]
    public void Lock_directory_equal_to_the_vault_root_refuses_startup() =>
        ShouldRefuseStartup(
            f => new() { ["Vault:LockDirectory"] = f.VaultDir },
            "lock files must never sync");

    [Fact]
    public void Audit_path_inside_the_vault_via_a_symlinked_ancestor_refuses_startup() =>
        ShouldRefuseStartup(f =>
        {
            var link = Path.Combine(f.OutsideDir, "innocent-looking");
            File.CreateSymbolicLink(link, f.VaultDir);
            return new() { ["Vault:AuditLogPath"] = Path.Combine(link, "audit.jsonl") };
        }, "audit log must never");

    [Fact]
    public void Lock_directory_inside_the_vault_via_a_symlinked_ancestor_refuses_startup() =>
        ShouldRefuseStartup(f =>
        {
            var link = Path.Combine(f.OutsideDir, "linked");
            File.CreateSymbolicLink(link, f.VaultDir);
            return new() { ["Vault:LockDirectory"] = Path.Combine(link, "locks") };
        }, "lock files must never sync");
}
