using System.ComponentModel;
using Knapper.Core;
using Knapper.Core.Transfer;
using ModelContextProtocol.Server;

namespace Knapper.Mcp.Tools;

// Transfer grants (docs/proposals/transfer-grants.md). Tool descriptions are
// the one channel every harness reliably shows an agent, so each stands on
// its own and leads with what must survive a cut (§13): when to use it, that
// a grant works once with the token off the command line, verify the hash,
// and what a curl failure means. The recipe itself travels in the grant's
// `fetch` field, which no client truncates.

[McpServerToolType]
public sealed class VaultDownloadPrepareTool(DownloadService downloads, TransferIdentity identity, ToolSupport support)
{
    public sealed record DownloadItem(
        [property: Description("Vault-relative path of the file to download")] string Path,
        [property: Description("Optional: the sha256 you expect; a mismatch fails the item [PreconditionFailed] and issues nothing")]
        string? ExpectSha256 = null);

    // ReadOnly = false although it writes no vault byte (§13): harnesses
    // auto-approve read-only tools, and this one exports bytes past the host
    // boundary — the human should be asked. And Mcp:ReadOnly DERIVES a
    // restricted surface from this flag, so `true` would put byte export on a
    // read-only deployment with nobody choosing it.
    [McpServerTool(Name = "vault_download_prepare", UseStructuredContent = true, ReadOnly = false, OpenWorld = false)]
    [Description(
        "Put vault files onto YOUR disk byte-exact: photos, PDFs, any binary, or a text file you need locally. " +
        "To read or edit a note, use vault_read/vault_edit instead. Returns per-item grants fetched with plain " +
        "curl from your shell via each grant's `fetch` recipe. RULES: (1) A grant works ONCE and expires in " +
        "minutes. (2) Keep the token OFF the command line — the recipe pipes it to curl on stdin. (3) Never use " +
        "the file until its sha256 matches the grant's; the recipe writes a private temp file and moves it into " +
        "place only after `sha256sum -c` passes. (4) curl exit 0: the hash check decides. 22: the server " +
        "refused — prepare again. 6/7/35/60: never connected, nothing was claimed; retry the same grant until " +
        "it expires, and if it repeats your sandbox is probably blocking the network: tell the user, do not " +
        "loop. 28/52/55/56: dropped mid-transfer — prepare again. Only the operator's allowed prefixes are " +
        "downloadable ([DownloadNotPermitted]); 1–16 items per call.")]
    public DownloadPrepareResult Prepare(
        [Description("Files to download (1–16)")] DownloadItem[] items) =>
        support.Run("vault_download_prepare", () => downloads.Prepare(
            identity.Current(),
            items is null ? null : [.. items.Select(i => new DownloadPrepareRequest(i.Path, i.ExpectSha256))],
            support.Caller()));
}

// ---- Upload: registered, not built (decision 4). Final shapes, so neither
// the names nor the schemas move when upload ships; every call refuses.

internal static class UploadNotBuilt
{
    internal const string Message =
        "uploads are not available on this server — use vault_create, vault_edit or vault_append";

    internal static T Refuse<T>(ToolSupport support, string tool) =>
        support.Run<T>(tool, () => throw new KnapperException(VaultErrorCode.UploadDisabled, Message));
}

[McpServerToolType]
public sealed class VaultUploadPrepareTool(ToolSupport support)
{
    public sealed record UploadItem(
        [property: Description("Vault-relative destination path")] string Path,
        [property: Description("sha256 of the local file, 64 hex characters")] string SourceSha256,
        [property: Description("Exact byte length of the local file")] long ByteLength,
        [property: Description("\"create\" (destination must be absent) or \"replace\"")] string Mode,
        [property: Description("Replace only: the destination's current sha256")] string? ExpectSha256 = null);

    [McpServerTool(Name = "vault_upload_prepare", UseStructuredContent = true, ReadOnly = true, OpenWorld = false)]
    [Description(
        "Upload grants are not available on this server yet: every call fails [UploadDisabled]. To put content " +
        "in the vault use vault_create, vault_edit or vault_append. When enabled, this authorizes uploading " +
        "local files byte-exact over plain curl — but whenever you hold a file's current bytes, an anchored " +
        "vault_edit is cheaper and safer; uploads are for new files, binaries and wholesale rewrites.")]
    public UploadPrepareResult Prepare(
        [Description("Files to upload (1–16)")] UploadItem[] items) =>
        UploadNotBuilt.Refuse<UploadPrepareResult>(support, "vault_upload_prepare");
}

[McpServerToolType]
public sealed class VaultUploadCommitTool(ToolSupport support)
{
    // Kept no longer than vault_create's own description: the two carry the
    // same convention clauses, so a deployment whose vault_create fits the
    // delivery budget is guaranteed this fits too (a test pins the order).
    [McpServerTool(Name = "vault_upload_commit", UseStructuredContent = true, ReadOnly = false, OpenWorld = false)]
    [Description(
        "Upload grants are not available on this server yet: every call fails [UploadDisabled]. Write vault " +
        "content with vault_create, vault_edit or vault_append instead.")]
    public UploadCommitResult Commit(
        [Description("Upload IDs from vault_upload_prepare")] string[] uploadIds) =>
        UploadNotBuilt.Refuse<UploadCommitResult>(support, "vault_upload_commit");
}

[McpServerToolType]
public sealed class VaultUploadStatusTool(ToolSupport support)
{
    [McpServerTool(Name = "vault_upload_status", UseStructuredContent = true, ReadOnly = true, OpenWorld = false)]
    [Description(
        "Upload grants are not available on this server yet: every call fails [UploadDisabled].")]
    public UploadStatusResult Status(
        [Description("Upload IDs from vault_upload_prepare")] string[] uploadIds) =>
        UploadNotBuilt.Refuse<UploadStatusResult>(support, "vault_upload_status");
}
