using System.Text.Json.Serialization;

namespace FileVault.GitHub;

// Wire shapes of the GitHub REST API (https://docs.github.com/rest), only the fields FileVault reads or sends.

internal sealed record ApiRepository(string DefaultBranch, bool Private);

internal sealed record ApiObjectRef(string Sha);

internal sealed record ApiRef(string Ref, ApiObjectRef Object);

internal sealed record ApiSignature(string? Name, string? Email, DateTimeOffset Date);

internal sealed record ApiGitCommit(
    string Sha,
    ApiObjectRef Tree,
    ApiSignature Author,
    ApiSignature Committer,
    string Message,
    List<ApiObjectRef> Parents);

internal sealed record ApiTreeEntry(string Path, string Mode, string Type, string Sha, long? Size);

internal sealed record ApiTree(string Sha, List<ApiTreeEntry> Tree, bool Truncated);

internal sealed record ApiCreatedBlob(string Sha);

internal sealed record ApiNewBlob(string Content, string Encoding);

/// <summary>
/// An entry of a new tree. <see cref="Sha"/> null deletes the path, so it is written even when null.
/// </summary>
internal sealed record ApiNewTreeEntry(
    string Path,
    string Mode,
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Sha);

internal sealed record ApiNewTree(string BaseTree, List<ApiNewTreeEntry> Tree);

internal sealed record ApiNewCommit(string Message, string Tree, List<string> Parents);

internal sealed record ApiUpdateRef(string Sha, bool Force);

internal sealed record ApiUser(string Login);

internal sealed record ApiCommitSummaryBody(ApiSignature Author, ApiSignature Committer, string Message);

internal sealed record ApiCommitFile(string Filename, string Status, string? PreviousFilename);

internal sealed record ApiCommit(
    string Sha,
    ApiCommitSummaryBody Commit,
    ApiUser? Author,
    List<ApiObjectRef> Parents,
    List<ApiCommitFile>? Files);

internal sealed record ApiBranch(string Name, ApiObjectRef Commit);

internal sealed record ApiComparison(string Status, int AheadBy, int BehindBy);

internal sealed record ApiError(string? Message);

// Git LFS batch API (https://github.com/git-lfs/git-lfs/blob/main/docs/api/batch.md)

internal sealed record LfsObjectRequest(string Oid, long Size);

internal sealed record LfsBatchRequest(string Operation, List<string> Transfers, List<LfsObjectRequest> Objects);

internal sealed record LfsAction(string Href, Dictionary<string, string>? Header);

internal sealed record LfsActions(LfsAction? Download);

internal sealed record LfsObjectError(int Code, string Message);

internal sealed record LfsObjectResponse(string Oid, long Size, LfsActions? Actions, LfsObjectError? Error);

internal sealed record LfsBatchResponse(List<LfsObjectResponse> Objects);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ApiRepository))]
[JsonSerializable(typeof(ApiRef))]
[JsonSerializable(typeof(ApiGitCommit))]
[JsonSerializable(typeof(ApiTree))]
[JsonSerializable(typeof(ApiCreatedBlob))]
[JsonSerializable(typeof(ApiNewBlob))]
[JsonSerializable(typeof(ApiNewTree))]
[JsonSerializable(typeof(ApiNewCommit))]
[JsonSerializable(typeof(ApiUpdateRef))]
[JsonSerializable(typeof(ApiCommit))]
[JsonSerializable(typeof(List<ApiCommit>))]
[JsonSerializable(typeof(List<ApiBranch>))]
[JsonSerializable(typeof(ApiComparison))]
[JsonSerializable(typeof(ApiError))]
[JsonSerializable(typeof(LfsBatchRequest))]
[JsonSerializable(typeof(LfsBatchResponse))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;
