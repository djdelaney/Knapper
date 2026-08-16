using System.Text.Json;
using Knapper.Core;

namespace Knapper.Mcp.Tests;

/// <summary>
/// Every tool's REAL response, checked against the schema that same tool
/// published. The manifest tests prove the schemas are loadable; this proves
/// they are TRUE — a client that validates structured content (the MCP spec
/// says clients SHOULD) rejects a correct answer that omits a property its
/// own schema requires, and the agent sees a broken tool, not a schema bug.
/// The SDK's own client does not validate, which is why every other wire test
/// in this suite stays green straight through that defect.
///
/// Schema and payload are both read off the wire, for the reason in
/// <see cref="RawMcp"/>: compared across layers, the check is meaningless.
/// This test mutates, so it runs on a private factory rather than the shared
/// class fixture.
/// </summary>
public class ToolResponseConformanceTests
{
    [Fact]
    public async Task Every_tool_returns_what_its_published_schema_promises()
    {
        using var factory = new KnapperMcpFactory(null);
        factory.Seed("conformance/edit-me.md", "before text\n");
        factory.Seed("conformance/append-me.md", "first line\n");
        factory.Seed("conformance/move-me.md", "movable\n");
        factory.Seed("conformance/delete-me.md", "deletable\n");
        factory.Seed("conformance/batch-me.md", "batch base\n");

        var session = await RawMcp.OpenAsync(factory.CreateClient());
        var schemas = (await session.ListToolsAsync()).ToDictionary(
            t => t.GetProperty("name").GetString()!,
            t => t.TryGetProperty("outputSchema", out var s) ? s : (JsonElement?)null,
            StringComparer.Ordinal);

        var problems = new List<string>();
        var covered = new HashSet<string>(StringComparer.Ordinal);

        async Task<JsonElement> Call(string tool, object arguments)
        {
            var result = await session.CallToolAsync(tool, arguments);
            (result.TryGetProperty("isError", out var isError) && isError.GetBoolean())
                .ShouldBeFalse($"{tool} errored: {result}");
            covered.Add(tool);
            var structured = result.TryGetProperty("structuredContent", out var content) ? content : (JsonElement?)null;
            problems.AddRange(ToolSchemaContract.FindMissingRequired(tool, schemas[tool], structured));
            return structured ?? default;
        }

        async Task<string> Sha(string path) =>
            (await Call("vault_read", new { path })).GetProperty("sha256").GetString()!;

        // Every call is fed arguments that produce a NON-EMPTY result: an
        // empty items array conforms to anything, so a sweep over an empty
        // vault would pass while proving nothing about item shapes.
        await Call("vault_files", new { maxResults = 5, includeSha = true });
        await Call("vault_stat", new { path = "Notes/Daily.md" });
        await Call("vault_batch_read", new
        {
            items = new object[]
            {
                new { path = "Notes/Daily.md" },
                new { path = "does-not-exist.md" }, // the per-item ERROR shape, which has its own required fields
            },
        });
        await Call("vault_search_frontmatter", new { field = "status" });
        foreach (var mode in new[] { "matches", "files", "counts" })
            await Call("vault_search", new { pattern = "needle", mode });
        // Context arrays are the only optional item fields that ever appear;
        // a mode sweep that never asks for context never serializes them.
        await Call("vault_search", new { pattern = "needle", contextBefore = 2, contextAfter = 2 });

        // Mutations, against this factory's throwaway vault.
        await Call("vault_mkdir", new { path = "conformance/new-dir" });
        await Call("vault_create", new { path = "conformance/created.md", text = "created\n" });
        await Call("vault_edit", new
        {
            path = "conformance/edit-me.md",
            expectSha256 = await Sha("conformance/edit-me.md"),
            edits = new[] { new { old = "before text", @new = "after text" } },
        });
        await Call("vault_append", new
        {
            path = "conformance/append-me.md",
            expectSha256 = await Sha("conformance/append-me.md"),
            text = "second line\n",
        });
        await Call("vault_move", new
        {
            sourcePath = "conformance/move-me.md",
            destinationPath = "conformance/moved.md",
            expectSourceSha256 = await Sha("conformance/move-me.md"),
        });
        await Call("vault_delete", new
        {
            path = "conformance/delete-me.md",
            expectSha256 = await Sha("conformance/delete-me.md"),
        });
        await Call("vault_batch", new
        {
            items = new[]
            {
                new
                {
                    kind = "append",
                    path = "conformance/batch-me.md",
                    expectSha256 = await Sha("conformance/batch-me.md"),
                    text = "batched\n",
                },
            },
        });

        problems.ShouldBeEmpty(string.Join("\n", problems));
        // A tool added to the surface without a case here would otherwise go
        // silently unchecked — the failure mode this whole file exists for.
        covered.ShouldBe(ToolNames.All, ignoreOrder: true);
    }
}
