namespace Knapper.Mcp.Tests;

/// <summary>
/// The fail-closed boot checks: misconfiguration refuses startup, never
/// surfaces on the first tool call. Containment — the bypasses a lexical
/// prefix check misses: lock/audit paths EQUAL to the vault root, and paths
/// reaching the vault through a symlinked ancestor. Ingress — an audience
/// pair that reads like a restriction but grants the whole surface. Vault
/// access — a root Knapper cannot write, which must refuse rather than boot
/// into a server that serves reads and fails every mutation.
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

    /// <summary>
    /// Pins that AccessOptions.Validate is actually WIRED into boot, which the
    /// unit tests over Validate itself cannot show.
    /// </summary>
    [Fact]
    public void Monitoring_audience_equal_to_the_owner_audience_refuses_startup() =>
        ShouldRefuseStartup(
            _ => new()
            {
                ["Mcp:Access:Enabled"] = "true",
                ["Mcp:Access:TeamDomain"] = "https://knapper-test.cloudflareaccess.com",
                ["Mcp:Access:Audience"] = "aud-owner",
                ["Mcp:Access:MonitoringAudience"] = "aud-owner",
            },
            "would carry the whole vault surface");

    /// <summary>
    /// The case-sensitivity probe is the first thing at boot to WRITE the vault
    /// root, so an unwritable root surfaces there. It must refuse with a
    /// diagnosis rather than escape as a raw filesystem exception — and must
    /// refuse rather than warn, since booting would serve reads while every
    /// mutation failed at run time.
    /// </summary>
    [Fact]
    public void An_unwritable_vault_root_refuses_startup_with_a_diagnosis()
    {
        using var factory = new KnapperMcpFactory();
        try
        {
            File.SetUnixFileMode(factory.VaultDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            // Mode bits do not bind root, so the scenario cannot be staged when
            // the suite runs as root; skip rather than assert something false.
            if (IsWritable(factory.VaultDir))
                return;

            var ex = Should.Throw<Exception>(() => factory.CreateClient());
            (ex.Message + ex.InnerException?.Message).ShouldContain("cannot write to the vault root");
        }
        finally
        {
            File.SetUnixFileMode(
                factory.VaultDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static bool IsWritable(string directory)
    {
        var probe = Path.Combine(directory, ".knapper-writable-check");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
