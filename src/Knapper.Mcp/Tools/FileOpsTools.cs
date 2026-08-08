using System.ComponentModel;
using Knapper.Core;
using Knapper.Core.Mutation;
using ModelContextProtocol.Server;

namespace Knapper.Mcp.Tools;

[McpServerToolType]
public sealed class VaultMoveTool(VaultMutationService mutations, ToolSupport support)
{
    [McpServerTool(Name = "vault_move", UseStructuredContent = true, ReadOnly = false, OpenWorld = false, Destructive = true)]
    [Description(
        "Move/rename a file. Requires the source's current SHA-256 and an ABSENT destination (a concurrent " +
        "appearance fails [AlreadyExists] — nothing is ever silently replaced). The destination directory must " +
        "already exist.")]
    public MutationResult Move(
        [Description("Vault-relative source path")] string sourcePath,
        [Description("Vault-relative destination path")] string destinationPath,
        [Description("SHA-256 of the source from your fresh read")] string expectSourceSha256) =>
        support.Run("vault_move", () =>
            mutations.Move(sourcePath, destinationPath, expectSourceSha256, support.Caller()));
}

[McpServerToolType]
public sealed class VaultDeleteTool(VaultMutationService mutations, ToolSupport support)
{
    [McpServerTool(Name = "vault_delete", UseStructuredContent = true, ReadOnly = false, OpenWorld = false, Destructive = true)]
    [Description(
        "SOFT delete: the file moves to .trash/ (structure preserved, collisions timestamped) — nothing is ever " +
        "hard-deleted. Requires the file's current SHA-256. The response names the trash location.")]
    public DeleteResult Delete(
        [Description("Vault-relative path")] string path,
        [Description("SHA-256 from your fresh read")] string expectSha256) =>
        support.Run("vault_delete", () => mutations.Delete(path, expectSha256, support.Caller()));
}

[McpServerToolType]
public sealed class VaultBatchTool(VaultMutationService mutations, ToolSupport support)
{
    public sealed record BatchOp(
        [property: Description("'edit', 'append', or 'create'")] string Kind,
        [property: Description("Vault-relative path (each path may appear only once)")] string Path,
        [property: Description("Required for edit/append: SHA-256 from a fresh read")] string? ExpectSha256 = null,
        [property: Description("edit: ordered anchored replacements")] EditOp[]? Edits = null,
        [property: Description("edit: strings that must exist before and survive after")] string[]? Guards = null,
        [property: Description("append/create: the text")] string? Text = null);

    [McpServerTool(Name = "vault_batch", UseStructuredContent = true, ReadOnly = false, OpenWorld = false, Destructive = true)]
    [Description(
        "Several mutations in one call. All locks are taken up front and EVERY item's hash/anchors/guards are " +
        "validated before the first write — one bad item fails the whole batch untouched. The apply phase is NOT " +
        "cross-file atomic: on a mid-batch I/O failure the response reports applied/failed/notAttempted per item.")]
    public BatchResult Batch(
        [Description("The operations (each path at most once)")] BatchOp[] items) =>
        support.Run("vault_batch", () => mutations.Batch(
            [.. items.Select(i => new BatchItem(
                i.Kind.ToLowerInvariant() switch
                {
                    "edit" => BatchItemKind.Edit,
                    "append" => BatchItemKind.Append,
                    "create" => BatchItemKind.Create,
                    _ => throw new KnapperException(VaultErrorCode.InvalidArgument,
                        $"kind must be 'edit', 'append', or 'create', got '{i.Kind}'"),
                },
                i.Path,
                i.ExpectSha256,
                i.Edits?.Select(e => new EditSpec(e.Old, e.New, e.Count)).ToList(),
                i.Guards,
                i.Text))],
            support.Caller()));
}
