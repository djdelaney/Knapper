using System.ComponentModel;
using Knapper.Core.Query;
using ModelContextProtocol.Server;

namespace Knapper.Mcp.Tools;

[McpServerToolType]
public sealed class VaultReadTool(VaultReadService reader, ToolSupport support)
{
    [McpServerTool(Name = "vault_read", UseStructuredContent = true, ReadOnly = true, OpenWorld = false)]
    [Description(
        "Read a file (whole, or an inclusive 1-based line range). Returns content, size, mtime, encoding, " +
        "totalLines, and sha256 — ALWAYS the whole file's hash, which is the expect_sha256 every mutation " +
        "requires. Read fresh immediately before you edit. Oversize files are rejected explicitly (TooLarge), " +
        "never silently truncated; non-UTF-8 files are refused (NotUtf8 — use vault_stat for metadata).")]
    public VaultReadResult Read(
        [Description("Vault-relative path")] string path,
        [Description("First line of a range (1-based, inclusive)")] int? startLine = null,
        [Description("Last line of a range (inclusive; clamped to the file's end, echoed back)")] int? endLine = null) =>
        support.Run("vault_read", () => reader.Read(path, startLine, endLine));
}

[McpServerToolType]
public sealed class VaultBatchReadTool(VaultReadService reader, ToolSupport support)
{
    public sealed record ReadItem(
        [property: Description("Vault-relative path")] string Path,
        [property: Description("First line (1-based, inclusive)")] int? StartLine = null,
        [property: Description("Last line (inclusive)")] int? EndLine = null);

    [McpServerTool(Name = "vault_batch_read", UseStructuredContent = true, ReadOnly = true, OpenWorld = false)]
    [Description(
        "Read several files/ranges in one call. Results are per-item: one unreadable file reports its own typed " +
        "error and never hides the others.")]
    public IReadOnlyList<VaultBatchReadItem> BatchRead(
        [Description("Paths (with optional line ranges) to read")] ReadItem[] items,
        CancellationToken ct = default) =>
        support.Run("vault_batch_read", () => reader.BatchRead(
            [.. items.Select(i => new VaultReadRequest(i.Path, i.StartLine, i.EndLine))], ct));
}

[McpServerToolType]
public sealed class VaultStatTool(VaultReadService reader, ToolSupport support)
{
    [McpServerTool(Name = "vault_stat", UseStructuredContent = true, ReadOnly = true, OpenWorld = false)]
    [Description(
        "Existence, type, size, mtime, encoding/text status, line count, and sha256 for a path — without the " +
        "body. The sha256 is valid as a mutation precondition.")]
    public VaultStatResult Stat(
        [Description("Vault-relative path")] string path) =>
        support.Run("vault_stat", () => reader.Stat(path));
}
