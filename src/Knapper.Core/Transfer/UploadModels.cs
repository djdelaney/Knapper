using Knapper.Core.Mutation;

namespace Knapper.Core.Transfer;

// The upload direction's wire shapes (docs/proposals/transfer-grants.md
// §14). NOT BUILT in this release: the three upload tools are registered with
// these final shapes so neither the tool names nor their schemas move when
// upload ships, and every call answers [UploadDisabled]. Nothing constructs
// these yet; they exist to be published as outputSchema.

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

public sealed record UploadStatusItem(
    string UploadId,
    string State,
    DateTimeOffset ExpiresAt,
    string Path,
    string SourceSha256,
    long ByteLength,
    MutationResult? Result,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record UploadStatusResult(IReadOnlyList<UploadStatusItem> Items);
