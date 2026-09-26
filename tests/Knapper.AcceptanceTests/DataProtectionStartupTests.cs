namespace Knapper.AcceptanceTests;

/// <summary>
/// The three ASP.NET Core Data Protection warnings a production start logs
/// when there is nowhere to keep a key ring — EventId 50 (in-memory
/// repository), 59 (no user profile) and 35 (no XML encryptor) — and the
/// setting that removes their cause, <c>Mcp:DataProtectionKeysPath</c>.
///
/// Harmless in themselves (nothing here uses Data Protection for anything
/// that matters), but the deployment holds a zero-warning baseline, and
/// permanent benign warnings are what train an operator to stop reading the
/// log. Real server processes, because the condition is process-wide: a HOME
/// the process cannot write, which is what systemd's ProtectHome gives the
/// service. (Unsetting HOME does not reproduce it — .NET falls back to the
/// passwd entry.)
/// </summary>
public sealed class DataProtectionStartupTests : IDisposable
{
    private readonly string _vaultDir = Wire.NewTempDir("knapper-dp-vault-");
    private readonly string _outsideDir = Wire.NewTempDir("knapper-dp-outside-");
    private readonly string _unwritableHome = Wire.NewTempDir("knapper-dp-home-");

    public DataProtectionStartupTests()
    {
        Wire.Seed(_vaultDir, "note.md", "x\n");
        File.SetUnixFileMode(_unwritableHome, UnixFileMode.UserRead | UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task Without_a_key_ring_path_the_warnings_appear()
    {
        // The reproduction. If this ever stops finding the warnings, the
        // test below proves nothing, so it is asserted rather than assumed.
        using var server = new AcceptanceServer(_vaultDir, _outsideDir, new Dictionary<string, string>
        {
            ["HOME"] = _unwritableHome,
        });
        await using (var client = await server.ConnectAsync()) { }
        await Task.Delay(500);

        server.Output.ShouldContain("Microsoft.AspNetCore.DataProtection");
    }

    /// <summary>
    /// With a persisted ring the two EVERY-start warnings (50, 59) are gone.
    /// EventId 35 remains, by design, once per key CREATION: the keys are
    /// stored unencrypted because they protect nothing. So the first start
    /// may log 35 and nothing else, and a restart on the same ring logs no
    /// Data Protection warning at all. (35 recurs when the framework rotates
    /// to a new key, ~every 90 days — not every start.)
    /// </summary>
    [Fact]
    public async Task With_a_key_ring_path_restarts_are_warning_free_and_the_ring_is_private()
    {
        var keys = Path.Combine(_outsideDir, "dataprotection-keys");
        var env = new Dictionary<string, string>
        {
            ["HOME"] = _unwritableHome,
            ["Mcp__DataProtectionKeysPath"] = keys,
        };

        string first;
        using (var server = new AcceptanceServer(_vaultDir, _outsideDir, env))
        {
            await using (var client = await server.ConnectAsync()) { }
            await Task.Delay(500);
            first = server.Output;
        }
        var dpLines = first.Split('\n').Where(l => l.Contains("Microsoft.AspNetCore.DataProtection")).ToList();
        dpLines.ShouldAllBe(l => l.Contains("\"EventId\":35,"), "only the key-creation notice may appear:\n" + first);
        Directory.GetFiles(keys, "key-*.xml").ShouldNotBeEmpty("the ring was persisted");

        using (var again = new AcceptanceServer(_vaultDir, _outsideDir, env))
        {
            await using (var client = await again.ConnectAsync()) { }
            await Task.Delay(500);
            again.Output.ShouldNotContain("Microsoft.AspNetCore.DataProtection", customMessage: again.Output);
        }

        (File.GetUnixFileMode(keys) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute))
            .ShouldBe(UnixFileMode.None, "the key ring directory must be owner-only");
    }

    [Fact]
    public void A_key_ring_path_inside_the_vault_refuses_startup()
    {
        var ex = Should.Throw<Exception>(() =>
        {
            using var server = new AcceptanceServer(_vaultDir, _outsideDir, new Dictionary<string, string>
            {
                ["Mcp__DataProtectionKeysPath"] = Path.Combine(_vaultDir, "keys"),
            });
        });
        ex.Message.ShouldContain("DataProtectionKeysPath");
        Directory.Exists(Path.Combine(_vaultDir, "keys")).ShouldBeFalse("nothing may be created in the vault first");
    }

    public void Dispose()
    {
        File.SetUnixFileMode(_unwritableHome, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Wire.TryDeleteDir(_vaultDir);
        Wire.TryDeleteDir(_outsideDir);
        Wire.TryDeleteDir(_unwritableHome);
    }
}
