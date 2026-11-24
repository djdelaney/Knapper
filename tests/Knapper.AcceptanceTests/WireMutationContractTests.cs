using ModelContextProtocol.Client;

namespace Knapper.AcceptanceTests;

/// <summary>
/// Brief §13 mutation-safety scenarios exercised as a black box over the
/// real socket against one real server process: batch all-or-nothing
/// validation, guard rejection, soft delete, the Sync conflict gate, and —
/// via the env-gated fault injector — an induced short write CAUGHT by the
/// post-write reopen/byte-compare. Disk state is asserted directly: the
/// wire receipt and the bytes must agree.
/// </summary>
public sealed class WireMutationContractTests : IAsyncLifetime
{
    private readonly string _vaultDir = Wire.NewTempDir("knapper-accept-vault-");
    private readonly string _outsideDir = Wire.NewTempDir("knapper-accept-outside-");
    private AcceptanceServer _server = null!;
    private McpClient _client = null!;

    public async Task InitializeAsync()
    {
        _server = new AcceptanceServer(_vaultDir, _outsideDir);
        _client = await _server.ConnectAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _client.DisposeAsync();
        _server.Dispose();
        Wire.TryDeleteDir(_vaultDir);
        Wire.TryDeleteDir(_outsideDir);
    }

    [Fact]
    public async Task Wire_exposes_the_locked_tool_surface_with_no_unconditional_write()
    {
        var names = (await _client.ListToolsAsync()).Select(t => t.Name).OrderBy(n => n).ToList();
        names.ShouldBe([
            "vault_append", "vault_batch", "vault_batch_read", "vault_create", "vault_delete",
            "vault_download_prepare", "vault_edit", "vault_files", "vault_lint", "vault_mkdir", "vault_move",
            "vault_read", "vault_search", "vault_search_frontmatter", "vault_stat",
            "vault_upload_commit", "vault_upload_prepare", "vault_upload_status",
        ]);
    }

    [Fact]
    public async Task Batch_with_one_bad_hash_mutates_nothing()
    {
        Wire.Seed(_vaultDir, "a.md", "alpha\n");
        Wire.Seed(_vaultDir, "b.md", "beta\n");
        var shaA = await Wire.ReadSha(_client, "a.md");

        var error = await Wire.CallError(_client, "vault_batch", new()
        {
            ["items"] = new object[]
            {
                new { kind = "edit", path = "a.md", expectSha256 = shaA,
                      edits = new[] { new { old = "alpha", @new = "ALPHA" } } },
                new { kind = "edit", path = "b.md", expectSha256 = new string('0', 64),
                      edits = new[] { new { old = "beta", @new = "BETA" } } },
            },
        });

        error.ShouldContain("[PreconditionFailed]");
        error.ShouldContain("nothing was mutated");
        File.ReadAllText(Path.Combine(_vaultDir, "a.md")).ShouldBe("alpha\n"); // the VALID item too
        File.ReadAllText(Path.Combine(_vaultDir, "b.md")).ShouldBe("beta\n");
    }

    [Fact]
    public async Task Guard_violation_rejects_with_the_file_untouched()
    {
        Wire.Seed(_vaultDir, "guarded.md", "# Title\nbody\n");
        var sha = await Wire.ReadSha(_client, "guarded.md");

        var error = await Wire.CallError(_client, "vault_edit", new()
        {
            ["path"] = "guarded.md",
            ["expectSha256"] = sha,
            ["edits"] = new[] { new { old = "body", @new = "new body" } },
            ["guards"] = new[] { "# A Heading That Is Not There" },
        });

        error.ShouldContain("[GuardViolation]");
        File.ReadAllText(Path.Combine(_vaultDir, "guarded.md")).ShouldBe("# Title\nbody\n");
    }

    [Fact]
    public async Task Soft_delete_lands_in_trash_with_structure_preserved()
    {
        Wire.Seed(_vaultDir, "Projects/old/done.md", "finished work\n");
        var sha = await Wire.ReadSha(_client, "Projects/old/done.md");

        var receipt = await Wire.CallOk(_client, "vault_delete", new()
        {
            ["path"] = "Projects/old/done.md",
            ["expectSha256"] = sha,
        });

        var trashPath = receipt.GetProperty("trashPath").GetString()!;
        trashPath.ShouldBe(".trash/Projects/old/done.md");
        File.Exists(Path.Combine(_vaultDir, "Projects/old/done.md")).ShouldBeFalse();
        File.ReadAllText(Path.Combine(_vaultDir, trashPath)).ShouldBe("finished work\n");
    }

    [Fact]
    public async Task A_sync_conflict_sibling_blocks_mutations_to_the_original()
    {
        Wire.Seed(_vaultDir, "Note.md", "content\n");
        var sha = await Wire.ReadSha(_client, "Note.md");
        // Exactly what Obsidian Sync leaves behind with --conflict-strategy conflict.
        Wire.Seed(_vaultDir, "Note (Conflicted copy 2026-08-09).md", "their content\n");

        var error = await Wire.CallError(_client, "vault_edit", new()
        {
            ["path"] = "Note.md",
            ["expectSha256"] = sha,
            ["edits"] = new[] { new { old = "content", @new = "changed" } },
        });

        error.ShouldContain("[MutationBlocked]");
        File.ReadAllText(Path.Combine(_vaultDir, "Note.md")).ShouldBe("content\n");
    }

    [Fact]
    public async Task An_induced_short_write_is_caught_by_the_post_write_byte_compare()
    {
        // A dedicated server with the fault injector armed for one filename:
        // the write path reports every success signal normally, but the file
        // receives only half the bytes — the vault's founding failure mode.
        // The reopen-and-byte-compare is the ONLY thing that may catch it.
        var vault = Wire.NewTempDir("knapper-accept-fault-vault-");
        var outside = Wire.NewTempDir("knapper-accept-fault-outside-");
        try
        {
            Wire.Seed(vault, "victim-note.md", "the full original content of the note\n");
            using var server = new AcceptanceServer(vault, outside,
                new Dictionary<string, string> { ["KNAPPER_FAULT_SHORT_WRITE"] = "victim-note" });
            await using var client = await server.ConnectAsync();

            var sha = await Wire.ReadSha(client, "victim-note.md");
            var error = await Wire.CallError(client, "vault_edit", new()
            {
                ["path"] = "victim-note.md",
                ["expectSha256"] = sha,
                ["edits"] = new[] { new { old = "original", @new = "edited" } },
            });

            // Never a success receipt for bytes that did not land.
            error.ShouldContain("[VerifyFailed]");

            // And an unfaulted file on the same server still round-trips.
            Wire.Seed(vault, "healthy.md", "fine\n");
            var healthySha = await Wire.ReadSha(client, "healthy.md");
            var receipt = await Wire.CallOk(client, "vault_edit", new()
            {
                ["path"] = "healthy.md",
                ["expectSha256"] = healthySha,
                ["edits"] = new[] { new { old = "fine", @new = "still fine" } },
            });
            receipt.GetProperty("verified").GetBoolean().ShouldBeTrue();
            File.ReadAllText(Path.Combine(vault, "healthy.md")).ShouldBe("still fine\n");
        }
        finally
        {
            Wire.TryDeleteDir(vault);
            Wire.TryDeleteDir(outside);
        }
    }

    // ---- security review 2026-11-24 F10: JSON nulls are a bad REQUEST -------

    /// <summary>
    /// Over the real wire, because the defect lived in the tool layer's own
    /// projections: a null array element was dereferenced there, before Core
    /// could refuse it, and reached the agent as [Internal] — "a server bug,
    /// give up" — for something it can fix in its own arguments.
    /// </summary>
    [Fact]
    public async Task A_null_array_element_is_an_invalid_argument_never_an_internal_error()
    {
        await Wire.CallOk(_client, "vault_create", new() { ["path"] = "Nulls.md", ["text"] = "base\n" });
        var sha = (await Wire.CallOk(_client, "vault_stat", new() { ["path"] = "Nulls.md" }))
            .GetProperty("sha256").GetString();

        var calls = new (string Tool, Dictionary<string, object?> Args)[]
        {
            ("vault_edit", new() { ["path"] = "Nulls.md", ["expectSha256"] = sha, ["edits"] = new object?[] { null } }),
            ("vault_batch", new() { ["items"] = new object?[] { null } }),
            ("vault_batch", new()
            {
                ["items"] = new object?[]
                {
                    new Dictionary<string, object?>
                    {
                        ["kind"] = "edit", ["path"] = "Nulls.md", ["expectSha256"] = sha,
                        ["edits"] = new object?[] { null },
                    },
                },
            }),
            ("vault_batch_read", new() { ["items"] = new object?[] { null } }),
            ("vault_search", new() { ["pattern"] = "base", ["includeGlobs"] = new object?[] { null } }),
            ("vault_search", new() { ["pattern"] = "base", ["extensions"] = new object?[] { null } }),
            ("vault_search", new() { ["pattern"] = "base", ["pathPrefixes"] = new object?[] { null } }),
            ("vault_files", new() { ["extensions"] = new object?[] { null } }),
            ("vault_lint", new() { ["checks"] = new object?[] { null } }),
        };
        foreach (var (tool, args) in calls)
        {
            var (ok, payload, error) = await Wire.Call(_client, tool, args);
            // vault_batch reports a bad item per item; everything else refuses the call.
            var text = ok ? payload!.Value.GetRawText() : error;
            text.ShouldNotContain("Internal", Case.Sensitive, $"{tool} answered a null element with a server error");
            text.ShouldContain("Invalid", Case.Sensitive, $"{tool}: {text}");
        }

        (await Wire.CallOk(_client, "vault_stat", new() { ["path"] = "Nulls.md" }))
            .GetProperty("sha256").GetString().ShouldBe(sha, "nothing was written");
    }
}
