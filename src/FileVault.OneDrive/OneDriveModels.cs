using System.Text.Json.Serialization;

namespace FileVault.OneDrive;

// Wire shapes of Microsoft Graph (https://learn.microsoft.com/graph/api/resources/driveitem), only the fields FileVault
// reads or sends.

internal sealed record GraphEmpty;

internal sealed record GraphFolderFacet(int? ChildCount);

internal sealed record GraphFileFacet(string? MimeType);

internal sealed record GraphItemReference(string? Id, string? DriveId, string? DriveType, string? Path);

internal sealed record GraphDriveItem(
    string Id,
    string Name,
    long? Size,
    DateTimeOffset? LastModifiedDateTime,
    GraphFolderFacet? Folder,
    GraphFileFacet? File,
    GraphItemReference? ParentReference,
    GraphEmpty? Image,
    GraphEmpty? Photo,
    GraphEmpty? Video,
    GraphEmpty? RemoteItem,
    GraphEmpty? Package,
    GraphEmpty? Root);

internal sealed record GraphItemPage(
    List<GraphDriveItem> Value,
    [property: JsonPropertyName("@odata.nextLink")] string? NextLink);

internal sealed record GraphQuota(long? Total, long? Used, long? Remaining, long? Deleted, string? State);

internal sealed record GraphIdentity(string? DisplayName);

internal sealed record GraphIdentitySet(GraphIdentity? User);

internal sealed record GraphDrive(string Id, string? DriveType, GraphQuota? Quota, GraphIdentitySet? Owner);

internal sealed record GraphNewFolder(
    string Name,
    GraphEmpty Folder,
    [property: JsonPropertyName("@microsoft.graph.conflictBehavior")] string ConflictBehavior);

internal sealed record GraphUploadableProperties(
    [property: JsonPropertyName("@microsoft.graph.conflictBehavior")] string ConflictBehavior,
    string Name);

internal sealed record GraphUploadSessionRequest(GraphUploadableProperties Item);

internal sealed record GraphUploadSession(string? UploadUrl, DateTimeOffset? ExpirationDateTime, List<string>? NextExpectedRanges);

internal sealed record GraphParentReference(string? Id, string? DriveId);

internal sealed record GraphItemUpdate(string? Name, GraphParentReference? ParentReference);

internal sealed record GraphCopyRequest(GraphParentReference ParentReference, string Name);

internal sealed record GraphError(string? Code, string? Message);

internal sealed record GraphErrorBody(GraphError? Error);

internal sealed record GraphAsyncJobStatus(string? Status, double? PercentageComplete, string? ResourceId, GraphError? Error);

internal sealed record GraphCreateLink(string Type, string Scope);

internal sealed record GraphSharingLink(string? Type, string? Scope, string? WebUrl);

internal sealed record GraphPermission(string Id, GraphSharingLink? Link, GraphItemReference? InheritedFrom);

internal sealed record GraphPermissionPage(
    List<GraphPermission> Value,
    [property: JsonPropertyName("@odata.nextLink")] string? NextLink);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GraphDriveItem))]
[JsonSerializable(typeof(GraphItemPage))]
[JsonSerializable(typeof(GraphDrive))]
[JsonSerializable(typeof(GraphNewFolder))]
[JsonSerializable(typeof(GraphUploadSessionRequest))]
[JsonSerializable(typeof(GraphUploadSession))]
[JsonSerializable(typeof(GraphItemUpdate))]
[JsonSerializable(typeof(GraphCopyRequest))]
[JsonSerializable(typeof(GraphErrorBody))]
[JsonSerializable(typeof(GraphAsyncJobStatus))]
[JsonSerializable(typeof(GraphCreateLink))]
[JsonSerializable(typeof(GraphPermission))]
[JsonSerializable(typeof(GraphPermissionPage))]
internal sealed partial class OneDriveJsonContext : JsonSerializerContext;

// Microsoft identity platform token endpoint (https://learn.microsoft.com/entra/identity-platform/v2-oauth2-auth-code-flow)

internal sealed record TokenResponse(
    string? AccessToken,
    string? TokenType,
    long? ExpiresIn,
    string? Scope,
    string? RefreshToken,
    string? Error,
    string? ErrorDescription);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(TokenResponse))]
internal sealed partial class OneDriveAuthJsonContext : JsonSerializerContext;
