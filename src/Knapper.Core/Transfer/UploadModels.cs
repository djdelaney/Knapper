using Knapper.Core.Mutation;

namespace Knapper.Core.Transfer;

// The upload direction's wire shapes (docs/proposals/transfer-grants.md §14).
// Item states are the seven upload states in lower case, plus "unknown" for
// an ID the caller cannot see.

public sealed record UploadGrant(
    string UploadId,
    string UploadUrl,
    string UploadToken,
    DateTimeOffset ExpiresAt,
    string Path,
    string SourceSha256,
    long ByteLength,
    string Mode,
    string? ExpectSha256,
    string Send);

public sealed record UploadPrepareItem(string Path, UploadGrant? Grant, string? ErrorCode, string? ErrorMessage);

public sealed record UploadPrepareResult(IReadOnlyList<UploadPrepareItem> Items);

public sealed record UploadCommitItem(
    string UploadId,
    string State,
    MutationResult? Result,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record UploadCommitResult(IReadOnlyList<UploadCommitItem> Items);

/// <summary>
/// One grant's state. The grant fields are null only for an ID this
/// principal cannot see (<c>UploadNotFound</c>, state <c>unknown</c>) —
/// unknown, forgotten and other-principal IDs look alike.
/// </summary>
public sealed record UploadStatusItem(
    string UploadId,
    string State,
    DateTimeOffset? ExpiresAt,
    string? Path,
    string? SourceSha256,
    long? ByteLength,
    MutationResult? Result,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record UploadStatusResult(IReadOnlyList<UploadStatusItem> Items);
