namespace Knapper.Core;

/// <summary>
/// The typed error vocabulary of the whole service. Every cap, rejection, and
/// failure the MCP surface reports maps to exactly one of these — the brief's
/// rule that "resource caps have protocol semantics" and that silent partial
/// success is forbidden starts here. Codes are wire-stable once the MCP layer
/// ships them; rename with the same care as a tool name.
/// </summary>
public enum VaultErrorCode
{
    /// <summary>Malformed path argument: empty, absolute, traversal (`..`), backslash, NUL.</summary>
    InvalidPath,

    /// <summary>Path resolves outside the vault root.</summary>
    PathOutsideVault,

    /// <summary>A path component is a symlink. Symlinks are rejected everywhere, always.</summary>
    SymlinkRejected,

    /// <summary>Path touches a control dir (.git, .obsidian, .trash) or a Knapper temp file.</summary>
    BannedPath,

    /// <summary>
    /// The path lies in a subtree the operator declared archived
    /// (<c>Vault:ArchivedPrefixes</c>) and the operation would CHANGE
    /// something already there. TERMINAL, like a banned path: retrying never
    /// succeeds. Creating and moving INTO an archived prefix are not this
    /// error — filing a superseded copy is the workflow the setting exists to
    /// protect, and banning it would ban archiving.
    /// </summary>
    PathArchived,

    /// <summary>Target does not exist (file, or a parent directory for create).</summary>
    NotFound,

    /// <summary>No-clobber create found the path already present.</summary>
    AlreadyExists,

    /// <summary>expect_sha256 did not match the bytes on disk — the file changed since the caller's read.</summary>
    PreconditionFailed,

    /// <summary>A guard string was absent before the edit or would not survive it.</summary>
    GuardViolation,

    /// <summary>An edit anchor matched a different number of times than declared.</summary>
    AnchorMismatch,

    /// <summary>File is not valid UTF-8; text operations refuse it.</summary>
    NotUtf8,

    /// <summary>Post-write reopen-and-byte-compare failed. Success receipts are never trusted without this check.</summary>
    VerifyFailed,

    /// <summary>Could not acquire the advisory lock within the deadline.</summary>
    LockTimeout,

    /// <summary>Mutations are blocked: a Sync conflict file exists for this note, or sync is unhealthy.</summary>
    MutationBlocked,

    /// <summary>Malformed query argument: bad regex, bad glob, bad line range, overlapping prefixes.</summary>
    InvalidArgument,

    /// <summary>Cursor is unparseable or belongs to a different query. Pages never silently restart.</summary>
    InvalidCursor,

    /// <summary>The time budget elapsed before any result could be produced. Distinct from an empty result.</summary>
    QueryTimeout,

    /// <summary>File exceeds the configured read cap. Explicit rejection — never a silently truncated "complete" file.</summary>
    TooLarge,

    /// <summary>
    /// The write would produce a file Obsidian Sync refuses to carry
    /// (<c>Sync__MaxFileBytes</c>). TERMINAL, unlike <see cref="MutationBlocked"/>:
    /// retrying never succeeds, because nothing about the vault's state is
    /// going to change. Distinct from <see cref="TooLarge"/>, which is the
    /// READ cap. Measured on CT 106 2026-08-13: Sync logs "File too large to
    /// sync (… max 5.00 MB)" and then "Fully synced" in the same millisecond,
    /// so the file is stranded locally with every health signal green.
    /// </summary>
    TooLargeToSync,

    /// <summary>Underlying filesystem or OS failure.</summary>
    IoError,

    // ---- transfer grants (docs/proposals/transfer-grants.md §14) ----------

    /// <summary>The download direction is turned off (<c>Download:Enabled</c> false). TERMINAL for this deployment.</summary>
    DownloadDisabled,

    /// <summary>The upload direction is turned off, or not built in this release. TERMINAL for this deployment.</summary>
    UploadDisabled,

    /// <summary>
    /// The path is outside <c>Download:AllowedPrefixes</c>, or the caller has
    /// no verified principal to bind a grant to. TERMINAL: the allowlist is
    /// operator configuration, never something a retry changes.
    /// </summary>
    DownloadNotPermitted,

    /// <summary>The shared transfer budget (bytes or grant slots) is full right now. Retry later; nothing was issued.</summary>
    DownloadCapacityExceeded,

    /// <summary>HTTP only: missing, malformed or wrong grant token, or an unknown grant ID. Reveals nothing about which.</summary>
    DownloadInvalidToken,

    /// <summary>HTTP only: the grant's lifetime ended before it was used. Prepare again.</summary>
    DownloadExpired,

    /// <summary>HTTP only: the grant has already been served once. Prepare again.</summary>
    DownloadAlreadyClaimed,

    /// <summary>No verified principal to bind an upload grant to. TERMINAL for the request.</summary>
    UploadNotPermitted,

    /// <summary>Unknown, expired-and-forgotten, or another principal's upload ID — deliberately indistinguishable.</summary>
    UploadNotFound,

    /// <summary>Commit asked of a grant whose bytes have not arrived (issued or receiving). Nothing was consumed.</summary>
    UploadNotReady,

    /// <summary><c>replace</c> mode while <c>Upload:AllowReplace</c> is off. TERMINAL for this deployment.</summary>
    UploadReplacementDisabled,

    /// <summary>The transfer budget, a grant slot, or the concurrent-receiver limit is full right now; nothing was claimed.</summary>
    UploadCapacityExceeded,

    /// <summary>HTTP only: missing, malformed or wrong upload token, or an unknown ID.</summary>
    UploadInvalidToken,

    /// <summary>The grant's lifetime ended before the transfer or the commit started. Prepare again.</summary>
    UploadExpired,

    /// <summary>HTTP only: the grant already took its one PUT. Call <c>vault_upload_status</c>; never re-send blindly.</summary>
    UploadAlreadyClaimed,

    /// <summary>The body was longer or shorter than the bound length. The grant is spent; re-hash and prepare again.</summary>
    UploadSizeMismatch,

    /// <summary>The body's sha256 is not the bound one — the local file changed. The grant is spent; re-hash and prepare again.</summary>
    UploadHashMismatch,

    /// <summary>The body did not arrive within <c>Upload:ReceiveTimeoutSeconds</c>. The grant is spent.</summary>
    UploadTransferTimeout,

    /// <summary>The connection failed mid-body. The grant is spent.</summary>
    UploadTransferFailed,

    /// <summary>
    /// The payload carries a credential-shaped string the git committer would
    /// refuse — one such file stops every vault commit. TERMINAL for the
    /// grant; the message carries the scanner's MASKED finding, never the
    /// matched text.
    /// </summary>
    SecretDetected,
}

/// <summary>
/// The one exception type crossing layer boundaries. The MCP layer translates
/// <see cref="Code"/> into a typed tool error; nothing above Core should need
/// to catch raw <see cref="IOException"/> to know what went wrong.
/// </summary>
public sealed class KnapperException(VaultErrorCode code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public VaultErrorCode Code { get; } = code;
}
