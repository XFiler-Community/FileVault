using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileVault.Dropbox;

// Wire shapes of the Dropbox HTTP API v2 (https://www.dropbox.com/developers/documentation/http/documentation),
// only the fields FileVault reads or sends. A union is an object with ".tag"; a struct member of a union is inlined
// next to the tag, a union member is nested under its name.

/// <summary>
/// A file, folder or deleted entry. Routes that return <c>FileMetadata</c> (upload, download) carry no tag: a file.
/// </summary>
internal sealed record ApiMetadata(
    [property: JsonPropertyName(".tag")] string? Tag,
    string Name,
    string? PathLower,
    string? PathDisplay,
    string? Id,
    DateTimeOffset? ClientModified,
    DateTimeOffset? ServerModified,
    string? Rev,
    long? Size,
    string? ContentHash);

internal sealed record ApiPathArg(string Path);

internal sealed record ApiCursorArg(string Cursor);

internal sealed record ApiMetadataResult(ApiMetadata Metadata);

internal sealed record ApiListFolderArg(string Path, bool Recursive, int? Limit);

internal sealed record ApiListFolderResult(List<ApiMetadata> Entries, string Cursor, bool HasMore);

internal sealed record ApiCreateFolderArg(string Path, bool Autorename);

internal sealed record ApiRelocationArg(string FromPath, string ToPath, bool Autorename);

internal sealed record ApiRelocationPath(string FromPath, string ToPath);

internal sealed record ApiRelocationBatchArg(List<ApiRelocationPath> Entries, bool Autorename);

internal sealed record ApiAsyncJobArg(string AsyncJobId);

/// <summary><c>success</c> holds the Metadata union; <c>failure</c> a RelocationBatchErrorEntry.</summary>
internal sealed record ApiRelocationBatchEntry(
    [property: JsonPropertyName(".tag")] string Tag,
    ApiMetadata? Success,
    JsonElement? Failure);

/// <summary>RelocationBatchV2Launch / RelocationBatchV2JobStatus: async_job_id · in_progress · complete.</summary>
internal sealed record ApiRelocationBatchStatus(
    [property: JsonPropertyName(".tag")] string Tag,
    string? AsyncJobId,
    List<ApiRelocationBatchEntry>? Entries);

internal sealed record ApiCommitInfo(string Path, string Mode, bool Autorename, bool Mute);

internal sealed record ApiUploadSessionStartArg(bool Close);

internal sealed record ApiUploadSessionStartResult(string SessionId);

internal sealed record ApiUploadSessionCursor(string SessionId, long Offset);

internal sealed record ApiUploadSessionAppendArg(ApiUploadSessionCursor Cursor, bool Close);

internal sealed record ApiUploadSessionFinishArg(ApiUploadSessionCursor Cursor, ApiCommitInfo Commit);

internal sealed record ApiSpaceAllocation(
    [property: JsonPropertyName(".tag")] string Tag,
    long? Allocated,
    long? Used,
    long? UserWithinTeamSpaceAllocated);

internal sealed record ApiSpaceUsage(long Used, ApiSpaceAllocation Allocation);

internal sealed record ApiSearchOptions(string? Path, int MaxResults, string FileStatus, bool FilenameOnly);

internal sealed record ApiSearchArg(string Query, ApiSearchOptions Options);

internal sealed record ApiMetadataV2([property: JsonPropertyName(".tag")] string Tag, ApiMetadata? Metadata);

internal sealed record ApiSearchMatch(ApiMetadataV2 Metadata);

internal sealed record ApiSearchResult(List<ApiSearchMatch> Matches, bool HasMore, string? Cursor);

internal sealed record ApiPathOrLink([property: JsonPropertyName(".tag")] string Tag, string Path);

internal sealed record ApiThumbnailArg(ApiPathOrLink Resource, string Format, string Size, string Mode);

internal sealed record ApiSharedLinkSettings(string Audience, string Access);

internal sealed record ApiCreateSharedLinkArg(string Path, ApiSharedLinkSettings Settings);

internal sealed record ApiSharedLink([property: JsonPropertyName(".tag")] string? Tag, string Url, string? PathLower);

internal sealed record ApiListSharedLinksArg(string Path, bool DirectOnly, string? Cursor);

internal sealed record ApiListSharedLinksResult(List<ApiSharedLink> Links, bool HasMore, string? Cursor);

internal sealed record ApiRevokeSharedLinkArg(string Url);

internal sealed record ApiErrorBody(string? ErrorSummary, JsonElement? Error);

// OAuth 2 (https://www.dropbox.com/developers/documentation/http/documentation#oauth2-token)

internal sealed record ApiTokenResponse(
    string AccessToken,
    string? TokenType,
    long? ExpiresIn,
    string? RefreshToken,
    string? Scope,
    string? AccountId,
    string? Uid);

internal sealed record ApiOAuthError(string? Error, string? ErrorDescription);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ApiMetadata))]
[JsonSerializable(typeof(ApiPathArg))]
[JsonSerializable(typeof(ApiCursorArg))]
[JsonSerializable(typeof(ApiMetadataResult))]
[JsonSerializable(typeof(ApiListFolderArg))]
[JsonSerializable(typeof(ApiListFolderResult))]
[JsonSerializable(typeof(ApiCreateFolderArg))]
[JsonSerializable(typeof(ApiRelocationArg))]
[JsonSerializable(typeof(ApiRelocationBatchArg))]
[JsonSerializable(typeof(ApiAsyncJobArg))]
[JsonSerializable(typeof(ApiRelocationBatchStatus))]
[JsonSerializable(typeof(ApiCommitInfo))]
[JsonSerializable(typeof(ApiUploadSessionStartArg))]
[JsonSerializable(typeof(ApiUploadSessionStartResult))]
[JsonSerializable(typeof(ApiUploadSessionAppendArg))]
[JsonSerializable(typeof(ApiUploadSessionFinishArg))]
[JsonSerializable(typeof(ApiSpaceUsage))]
[JsonSerializable(typeof(ApiSearchArg))]
[JsonSerializable(typeof(ApiSearchResult))]
[JsonSerializable(typeof(ApiThumbnailArg))]
[JsonSerializable(typeof(ApiCreateSharedLinkArg))]
[JsonSerializable(typeof(ApiSharedLink))]
[JsonSerializable(typeof(ApiListSharedLinksArg))]
[JsonSerializable(typeof(ApiListSharedLinksResult))]
[JsonSerializable(typeof(ApiRevokeSharedLinkArg))]
[JsonSerializable(typeof(ApiErrorBody))]
[JsonSerializable(typeof(ApiTokenResponse))]
[JsonSerializable(typeof(ApiOAuthError))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class DropboxJsonContext : JsonSerializerContext;
