using System.ComponentModel;
using Knapper.Core.Mutation;
using ModelContextProtocol.Server;

namespace Knapper.Mcp.Tools;

/// <summary>Wire shape of one anchored edit (see vault_edit).</summary>
public sealed record EditOp(
    [property: Description("Exact text that must occur exactly 'count' times")] string Old,
    [property: Description("Replacement text")] string New,
    [property: Description("How many occurrences 'old' must have (default 1); all are replaced")] int Count = 1);

[McpServerToolType]
public sealed class VaultEditTool(VaultMutationService mutations, ToolSupport support)
{
    [McpServerTool(Name = "vault_edit", UseStructuredContent = true, ReadOnly = false, OpenWorld = false, Destructive = true)]
    [Description(
        "Anchored conditional edit. Requires expectSha256 from a FRESH vault_read; under the vault's cross-process " +
        "lock the file is re-read, the hash compared, each edit's 'old' matched exactly 'count' times, guards " +
        "checked before and after, and the write verified by reopening and byte-comparing. On " +
        "[PreconditionFailed] the file changed since your read: re-read and rebuild the edit against current " +
        "content — NEVER retry with the old base. Edits apply sequentially (later anchors see earlier " +
        "results)." + VaultConventions.Writing + VaultConventions.ArchivedWrites)]
    public MutationResult Edit(
        [Description("Vault-relative path")] string path,
        [Description("SHA-256 from your fresh read")] string expectSha256,
        [Description("Ordered anchored replacements")] EditOp[] edits,
        [Description("Strings that must exist before AND survive after the edit")] string[]? guards = null) =>
        support.Run("vault_edit", () => mutations.Edit(
            path, expectSha256,
            [.. edits.Select(e => new EditSpec(e.Old, e.New, e.Count))],
            guards, support.Caller()));
}

[McpServerToolType]
public sealed class VaultAppendTool(VaultMutationService mutations, ToolSupport support)
{
    [McpServerTool(Name = "vault_append", UseStructuredContent = true, ReadOnly = false, OpenWorld = false)]
    [Description(
        "Append text to an existing file under the same lock + hash discipline as vault_edit (never an unlocked " +
        "read-then-rewrite). Include a leading newline yourself if you need one." +
        VaultConventions.Writing + VaultConventions.ArchivedWrites)]
    public MutationResult Append(
        [Description("Vault-relative path")] string path,
        [Description("SHA-256 from your fresh read")] string expectSha256,
        [Description("Text to append (non-empty)")] string text) =>
        support.Run("vault_append", () => mutations.Append(path, expectSha256, text, support.Caller()));
}

[McpServerToolType]
public sealed class VaultCreateTool(VaultMutationService mutations, ToolSupport support)
{
    [McpServerTool(Name = "vault_create", UseStructuredContent = true, ReadOnly = false, OpenWorld = false)]
    [Description(
        "Create a new file atomically — cannot replace a file that appears concurrently ([AlreadyExists]). " +
        "The parent directory must already exist: create it first with vault_mkdir (a deliberate act)." +
        VaultConventions.Placement + VaultConventions.Writing)]
    public MutationResult Create(
        [Description("Vault-relative path")] string path,
        [Description("File content (may be empty)")] string text) =>
        support.Run("vault_create", () => mutations.Create(path, text, support.Caller()));
}

[McpServerToolType]
public sealed class VaultMkdirTool(VaultMutationService mutations, ToolSupport support)
{
    [McpServerTool(Name = "vault_mkdir", UseStructuredContent = true, ReadOnly = false, OpenWorld = false)]
    [Description(
        "Create ONE directory level; the parent must already exist. Folder creation is deliberate, never " +
        "implied." + VaultConventions.Placement)]
    public string Mkdir(
        [Description("Vault-relative directory path")] string path) =>
        support.Run("vault_mkdir", () =>
        {
            mutations.CreateDirectory(path, support.Caller());
            return path;
        });
}
