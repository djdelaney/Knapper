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

// ---- Upload (§4). Prepare binds destination and exact source bytes; the PUT
// only stages; commit lands them through the ordinary conditional write.

[McpServerToolType]
public sealed class VaultUploadPrepareTool(UploadService uploads, TransferIdentity identity, ToolSupport support)
{
    public sealed record UploadItem(
        [property: Description("Vault-relative destination path")] string Path,
        [property: Description("sha256 of the local file, 64 hex characters")] string SourceSha256,
        [property: Description("Exact byte length of the local file")] long ByteLength,
        [property: Description("\"create\" (destination must be absent) or \"replace\"")] string Mode,
        [property: Description("Replace only: the destination's current sha256")] string? ExpectSha256 = null);

    // ReadOnly: it writes no vault byte — the PUT only stages, and the write
    // happens at vault_upload_commit, which is a writer and prompts.
    [McpServerTool(Name = "vault_upload_prepare", UseStructuredContent = true, ReadOnly = true, OpenWorld = false)]
    [Description(
        "Upload files from YOUR disk into the vault byte-exact: new files, binaries (photos, PDFs) and wholesale " +
        "rewrites. If you hold a file's current bytes, an anchored vault_edit is cheaper and safer — use it for " +
        "evolving text. RULES: (1) Hash first: sourceSha256 = sha256sum of the local file, byteLength = its size; " +
        "the server binds both. (2) Each grant works ONCE and expires in minutes; keep the token OFF the command " +
        "line — run the grant's `send` recipe, which pipes it to curl on stdin. (3) Nothing lands until " +
        "vault_upload_commit; the PUT only stages bytes. (4) curl exit 0: commit. 22: read the printed {code} — " +
        "UploadHashMismatch or UploadSizeMismatch mean the file changed: re-hash and prepare again; " +
        "UploadAlreadyClaimed: call vault_upload_status. 6/7/35/60: never connected, nothing was claimed; retry the " +
        "same grant, and if it repeats your sandbox is probably blocking the network: tell the user, do not loop. " +
        "28/52/55/56: dropped mid-transfer — call vault_upload_status, never re-send blindly. mode \"create\" needs " +
        "the path absent; \"replace\" needs expectSha256 from a fresh read and is off unless the operator allows it " +
        "([UploadReplacementDisabled]). Parent folders must exist. 1–16 items per call.")]
    public UploadPrepareResult Prepare(
        [Description("Files to upload (1–16)")] UploadItem[] items) =>
        support.Run("vault_upload_prepare", () => uploads.Prepare(
            identity.Current(),
            items is null ? null : [.. items.Select(i => i is null ? null! :
                new UploadPrepareRequest(i.Path, i.SourceSha256, i.ByteLength, i.Mode, i.ExpectSha256))],
            support.Caller()));
}

[McpServerToolType]
public sealed class VaultUploadCommitTool(UploadService uploads, TransferIdentity identity, ToolSupport support)
{
    // Kept no longer than vault_create's own description: the two carry the
    // same convention clauses, so a deployment whose vault_create fits the
    // delivery budget is guaranteed this fits too (a test pins the order).
    [McpServerTool(Name = "vault_upload_commit", UseStructuredContent = true, ReadOnly = false, OpenWorld = false)]
    [Description(
        "Land staged uploads (state ready) through the normal conditional write; the receipt hash equals the " +
        "source hash. Items commit independently: a stale base or a secret fails only its own item.")]
    public UploadCommitResult Commit(
        [Description("Upload IDs from vault_upload_prepare")] string[] uploadIds) =>
        support.Run("vault_upload_commit", () => uploads.Commit(identity.Current(), uploadIds, support.Caller()));
}

[McpServerToolType]
public sealed class VaultUploadStatusTool(UploadService uploads, TransferIdentity identity, ToolSupport support)
{
    [McpServerTool(Name = "vault_upload_status", UseStructuredContent = true, ReadOnly = true, OpenWorld = false)]
    [Description(
        "State of upload grants — issued, receiving, ready, committing, succeeded, failed, expired, or unknown — and " +
        "the outcome once terminal. Use it after an uncertain transfer (curl exit 28/52/55/56) instead of re-sending: " +
        "ready means commit, receiving means wait, failed or expired means prepare again. It never starts a write.")]
    public UploadStatusResult Status(
        [Description("Upload IDs from vault_upload_prepare")] string[] uploadIds) =>
        support.Run("vault_upload_status", () => uploads.Status(identity.Current(), uploadIds));
}
