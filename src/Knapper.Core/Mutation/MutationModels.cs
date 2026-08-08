namespace Knapper.Core.Mutation;

/// <summary>One anchored replacement: <c>Old</c> must occur exactly <c>Count</c> times when applied.</summary>
public sealed record EditSpec(string Old, string New, int Count = 1);

/// <summary>Who asked. Filled by the MCP layer from the authenticated request; null in local/admin use.</summary>
public sealed record AuditContext(string? Client, string? RequestId)
{
    public static readonly AuditContext None = new(null, null);
}

public sealed record MutationResult(
    string Path,
    string? OldSha256,
    string NewSha256,
    long BytesBefore,
    long BytesAfter,
    /// <summary>Always true on return — the reopen-and-byte-compare passed. Present so receipts SAY what was checked.</summary>
    bool Verified,
    long Generation);

public sealed record DeleteResult(
    string Path,
    /// <summary>Vault-relative path inside .trash/ where the file now lives.</summary>
    string TrashPath,
    string Sha256,
    long Generation);

public enum BatchItemKind
{
    Edit,
    Append,
    Create,
}

public sealed record BatchItem(
    BatchItemKind Kind,
    string Path,
    string? ExpectSha256 = null,
    IReadOnlyList<EditSpec>? Edits = null,
    IReadOnlyList<string>? Guards = null,
    string? Text = null);

public enum BatchItemStatus
{
    Applied,
    Failed,
    /// <summary>An earlier item failed during the apply phase; this one was never started.</summary>
    NotAttempted,
}

public sealed record BatchItemResult(
    string Path,
    BatchItemStatus Status,
    string? NewSha256,
    VaultErrorCode? ErrorCode,
    string? ErrorMessage);

/// <summary>
/// Batch outcome. NOT cross-file atomic (brief §7): all preconditions,
/// anchors, and guards are validated under the locks before the first write,
/// so a bad item fails the whole batch untouched — but an apply-phase
/// failure (I/O mid-batch) leaves earlier items applied, reported here
/// per-item, with git history as the recovery path.
/// </summary>
public sealed record BatchResult(IReadOnlyList<BatchItemResult> Items, bool AllApplied);
