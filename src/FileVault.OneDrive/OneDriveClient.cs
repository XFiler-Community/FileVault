using System.Net;
using FileVault.Core;

namespace FileVault.OneDrive;

/// <summary>
/// One signed-in account's OneDrive (<c>/me/drive</c>) over Microsoft Graph. Items and providers made from one client
/// are "the same drive": copy, move and rename between them stay on the server. Authentication is the app's: the client
/// only asks <see cref="OneDriveAccessTokenProvider"/> for a token (see <see cref="OneDriveAuth"/> and
/// <see cref="OneDriveTokenSource"/> for a ready sign-in).
/// </summary>
/// <remarks>
/// Full names: <c>onedrive:root</c> is the drive root, <c>onedrive:&lt;item-id&gt;</c> any other file or folder (ids are
/// stable across renames and moves), and <c>onedrive:&lt;parent-id&gt;/&lt;name&gt;</c> a destination that may not exist
/// yet (what <see cref="IFileProvider.ResolveDestinationFileAsync"/> returns and copy/move take). Folders shared with
/// the user from other drives (items with a <c>remoteItem</c> facet) are not listed: they live outside <c>/me/drive</c>.
/// </remarks>
public sealed partial class OneDriveClient
{
    public const string Scheme = "onedrive:";
    public const string RootId = "root";

    internal const string ItemSelect =
        "id,name,size,lastModifiedDateTime,folder,file,image,photo,video,parentReference,remoteItem,package,root";

    private readonly HttpClient _http;
    private readonly OneDriveAccessTokenProvider _accessToken;
    private readonly OneDriveClientOptions _options;
    private GraphItemReference? _root;

    public OneDriveClient(HttpClient http, OneDriveAccessTokenProvider accessToken, OneDriveClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(accessToken);

        _http = http;
        _accessToken = accessToken;
        _options = options ?? new OneDriveClientOptions();
        _options.Validate();
    }

    internal OneDriveClientOptions Options => _options;

    /// <summary>The drive root with its quota (<c>GET /me/drive</c>).</summary>
    public async Task<OneDriveDriveItem> GetDriveAsync(CancellationToken ct = default)
    {
        var drive = await GetJsonAsync("me/drive?$select=id,driveType,quota,owner", OneDriveJsonContext.Default.GraphDrive,
            ct).ConfigureAwait(false);
        return new OneDriveDriveItem(this, drive);
    }

    /// <summary>A provider for a folder by its id (<see cref="RootId"/> — the drive root). Does not check that it exists.</summary>
    public OneDriveFileProvider CreateProvider(string folderId = RootId, string? name = null) => new(this, folderId, name);

    /// <summary>A file or folder by id; null when there is none (or it is in the recycle bin).</summary>
    public async Task<IFileProviderItem?> GetItemAsync(string itemId, CancellationToken ct = default)
    {
        var item = await TryGetJsonAsync($"{ItemPath(itemId)}?$select={ItemSelect}",
            OneDriveJsonContext.Default.GraphDriveItem, ct).ConfigureAwait(false);
        return item is null ? null : ToItem(item);
    }

    /// <summary>The item id of a full name made by this library; false for a destination path (<c>onedrive:&lt;parent&gt;/&lt;name&gt;</c>).</summary>
    public static bool TryParseFullName(string fullName, out string itemId)
    {
        itemId = "";

        if (!fullName.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = fullName[Scheme.Length..];

        if (rest.Length == 0 || rest.Contains('/'))
            return false;

        itemId = rest;
        return true;
    }

    // ── Addressing ──────────────────────────────────────────────────────────

    internal static string ItemPath(string id) =>
        id == RootId ? "me/drive/root" : $"me/drive/items/{Uri.EscapeDataString(id)}";

    /// <summary>Path addressing below a folder: <c>…/items/{id}:/a/b</c>. Names are escaped segment by segment.</summary>
    internal static string ChildPath(string parentId, string relativePath) =>
        $"{ItemPath(parentId)}:/{string.Join('/', relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString))}";

    internal static string FullNameOf(string id) => $"{Scheme}{id}";

    internal IFileProviderItem ToItem(GraphDriveItem item) =>
        item.Folder is not null || item.Root is not null
            ? new OneDriveFolderItem(this, item)
            : new OneDriveFileItem(this, item);

    /// <summary>The root's real id and the drive id; Graph wants both in a <c>parentReference</c>. Read once.</summary>
    internal async Task<GraphItemReference> GetRootReferenceAsync(CancellationToken ct)
    {
        if (_root is { } cached)
            return cached;

        var root = await GetJsonAsync($"{ItemPath(RootId)}?$select=id,parentReference",
            OneDriveJsonContext.Default.GraphDriveItem, ct).ConfigureAwait(false);

        return _root = new GraphItemReference(root.Id, root.ParentReference?.DriveId, root.ParentReference?.DriveType, null);
    }

    internal async Task<string> ResolveIdAsync(string id, CancellationToken ct) =>
        id == RootId ? (await GetRootReferenceAsync(ct).ConfigureAwait(false)).Id! : id;

    // ── Items ───────────────────────────────────────────────────────────────

    internal async IAsyncEnumerable<GraphDriveItem> ListChildrenAsync(string folderId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        string? next = $"{ItemPath(folderId)}/children?$top={_options.PageSize}&$select={ItemSelect}";

        while (next is not null)
        {
            var page = await GetJsonAsync(next, OneDriveJsonContext.Default.GraphItemPage, ct).ConfigureAwait(false);

            foreach (var item in page.Value)
                yield return item;

            next = page.NextLink;
        }
    }

    /// <summary>The item at <paramref name="relativePath"/> below a folder; null when there is none.</summary>
    internal Task<GraphDriveItem?> TryGetByPathAsync(string folderId, string relativePath, CancellationToken ct) =>
        TryGetJsonAsync($"{ChildPath(folderId, relativePath)}?$select={ItemSelect}",
            OneDriveJsonContext.Default.GraphDriveItem, ct);

    internal Task<GraphDriveItem> CreateFolderAsync(string parentId, string name, CancellationToken ct) =>
        SendJsonAsync(HttpMethod.Post, $"{ItemPath(parentId)}/children",
            new GraphNewFolder(name, new GraphEmpty(), "fail"),
            OneDriveJsonContext.Default.GraphNewFolder, OneDriveJsonContext.Default.GraphDriveItem, ct);

    /// <summary>Renames and/or moves an item in one PATCH; a taken name fails with <c>nameAlreadyExists</c>.</summary>
    internal async Task<GraphDriveItem> UpdateAsync(string id, string? newName, string? newParentId, CancellationToken ct)
    {
        var parent = newParentId is null
            ? null
            : new GraphParentReference(await ResolveIdAsync(newParentId, ct).ConfigureAwait(false), null);

        return await SendJsonAsync(HttpMethod.Patch, ItemPath(id), new GraphItemUpdate(newName, parent),
            OneDriveJsonContext.Default.GraphItemUpdate, OneDriveJsonContext.Default.GraphDriveItem, ct).ConfigureAwait(false);
    }

    /// <summary><c>DELETE</c> moves the item to the OneDrive recycle bin; it can be restored from there.</summary>
    internal Task MoveToRecycleBinAsync(string id, CancellationToken ct) =>
        SendWithoutBodyAsync(HttpMethod.Delete, ItemPath(id), ct);

    /// <summary>
    /// Deletes past the recycle bin. Microsoft documents <c>permanentDelete</c> for personal and work/school drives; a
    /// drive that refuses it (405/501, or <c>notSupported</c> / <c>invalidRequest</c>) gets
    /// <see cref="NotSupportedException"/>, never a silent move to the recycle bin.
    /// </summary>
    internal async Task DeletePermanentlyAsync(string id, CancellationToken ct)
    {
        try
        {
            await SendWithoutBodyAsync(HttpMethod.Post, $"{ItemPath(id)}/permanentDelete", ct).ConfigureAwait(false);
        }
        catch (OneDriveApiException e) when (e.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented
                                             || e.Code is "notSupported" or "invalidRequest")
        {
            throw new NotSupportedException(
                "This OneDrive does not allow deleting past the recycle bin; nothing was deleted.", e);
        }
    }

    /// <summary>
    /// Items whose name (or content — Graph's index is fuzzy) matches <paramref name="query"/> below a folder. Graph's
    /// documentation no longer promises that a folder's <c>search</c> stays inside it, so every result is checked: by
    /// <c>parentReference.path</c> when Graph gives it, else by walking parent ids up (each folder asked once).
    /// </summary>
    internal async IAsyncEnumerable<GraphDriveItem> SearchAsync(string folderId, string query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var root = await GetRootReferenceAsync(ct).ConfigureAwait(false);
        var scopeId = await ResolveIdAsync(folderId, ct).ConfigureAwait(false);
        var wholeDrive = scopeId == root.Id;
        string? scopePath = null;

        if (!wholeDrive)
        {
            var folder = await GetJsonAsync($"{ItemPath(scopeId)}?$select=id,name,parentReference",
                OneDriveJsonContext.Default.GraphDriveItem, ct).ConfigureAwait(false);
            if (folder.ParentReference?.Path is { } parentPath)
                scopePath = $"{Uri.UnescapeDataString(parentPath)}/{folder.Name}";
        }

        var inside = new HashSet<string>(StringComparer.Ordinal) { scopeId };
        var outside = new HashSet<string>(StringComparer.Ordinal);
        if (!wholeDrive && root.Id is { } rootId)
            outside.Add(rootId);

        var escaped = Uri.EscapeDataString(query.Replace("'", "''"));
        string? next = $"{ItemPath(folderId)}/search(q='{escaped}')?$top={_options.PageSize}&$select={ItemSelect}";

        while (next is not null)
        {
            var page = await GetJsonAsync(next, OneDriveJsonContext.Default.GraphItemPage, ct).ConfigureAwait(false);

            foreach (var item in page.Value)
            {
                if (item.RemoteItem is not null || item.Root is not null || item.Id == scopeId)
                    continue;

                if (wholeDrive || await IsBelowAsync(item, scopePath, inside, outside, ct).ConfigureAwait(false))
                    yield return item;
            }

            next = page.NextLink;
        }
    }

    private async Task<bool> IsBelowAsync(GraphDriveItem item, string? scopePath, HashSet<string> inside,
        HashSet<string> outside, CancellationToken ct)
    {
        if (scopePath is not null && item.ParentReference?.Path is { } path)
        {
            var parentPath = Uri.UnescapeDataString(path);
            return parentPath.Equals(scopePath, StringComparison.OrdinalIgnoreCase)
                   || parentPath.StartsWith(scopePath + "/", StringComparison.OrdinalIgnoreCase);
        }

        var chain = new List<string>();
        var current = item.ParentReference?.Id;

        for (var depth = 0; current is not null && depth < 256; depth++)
        {
            if (inside.Contains(current))
            {
                inside.UnionWith(chain);
                return true;
            }

            if (outside.Contains(current))
                break;

            chain.Add(current);
            var parent = await TryGetJsonAsync($"{ItemPath(current)}?$select=id,parentReference",
                OneDriveJsonContext.Default.GraphDriveItem, ct).ConfigureAwait(false);
            current = parent?.ParentReference?.Id;
        }

        outside.UnionWith(chain);
        return false;
    }

    // ── Sharing ─────────────────────────────────────────────────────────────

    private static bool IsOwnAnonymousViewLink(GraphPermission permission) =>
        permission.Link is { Type: "view", Scope: "anonymous", WebUrl: not null }
        && permission.InheritedFrom is not { Id: not null } and not { Path: not null };

    private async Task<List<GraphPermission>> ListPermissionsAsync(string id, CancellationToken ct)
    {
        var permissions = new List<GraphPermission>();
        string? next = $"{ItemPath(id)}/permissions";

        while (next is not null)
        {
            var page = await GetJsonAsync(next, OneDriveJsonContext.Default.GraphPermissionPage, ct).ConfigureAwait(false);
            permissions.AddRange(page.Value);
            next = page.NextLink;
        }

        return permissions;
    }

    /// <summary>
    /// The item's own anonymous view link (one inherited from a parent folder would open the folder, so it does not
    /// count); <c>createLink</c> view/anonymous when there is none. Work/school tenants may forbid anonymous links:
    /// Graph's refusal comes back as the error.
    /// </summary>
    internal async Task<Uri> GetPublicLinkAsync(string id, CancellationToken ct)
    {
        var existing = (await ListPermissionsAsync(id, ct).ConfigureAwait(false)).FirstOrDefault(IsOwnAnonymousViewLink);

        if (existing is not null)
            return new Uri(existing.Link!.WebUrl!);

        var created = await SendJsonAsync(HttpMethod.Post, $"{ItemPath(id)}/createLink",
            new GraphCreateLink("view", "anonymous"), OneDriveJsonContext.Default.GraphCreateLink,
            OneDriveJsonContext.Default.GraphPermission, ct).ConfigureAwait(false);

        return new Uri(created.Link?.WebUrl
                       ?? throw new OneDriveApiException(HttpStatusCode.OK, null, "Microsoft Graph returned a link without a URL."));
    }

    /// <summary>Deletes the item's own anonymous view links. Edit links and links of a parent folder stay.</summary>
    internal async Task RemovePublicLinksAsync(string id, CancellationToken ct)
    {
        foreach (var permission in (await ListPermissionsAsync(id, ct).ConfigureAwait(false)).Where(IsOwnAnonymousViewLink))
            await SendWithoutBodyAsync(HttpMethod.Delete,
                $"{ItemPath(id)}/permissions/{Uri.EscapeDataString(permission.Id)}", ct).ConfigureAwait(false);
    }

    // ── Thumbnails ──────────────────────────────────────────────────────────

    /// <summary>
    /// <c>thumbnails/0/c{size}x{size}/content</c> — a picture that fits a size×size box, aspect kept. A drive that
    /// refuses a custom size (400) gets the nearest standard one (small 96, medium 176, large 800). 404: no picture.
    /// </summary>
    internal async Task<Stream?> OpenThumbnailAsync(string id, int size, CancellationToken ct)
    {
        size = Math.Clamp(size, 16, 2048);

        var (stream, refused) = await TryDownloadThumbnailAsync(id, $"c{size}x{size}", ct).ConfigureAwait(false);

        if (refused)
            (stream, _) = await TryDownloadThumbnailAsync(id, size <= 96 ? "small" : size <= 176 ? "medium" : "large", ct)
                .ConfigureAwait(false);

        return stream;
    }

    /// <summary>Refused: the drive does not draw this size (400). Null stream without refusal: no picture (404).</summary>
    private async Task<(Stream? Stream, bool Refused)> TryDownloadThumbnailAsync(string id, string size,
        CancellationToken ct)
    {
        using var response = await GetFollowingRedirectAsync($"{ItemPath(id)}/thumbnails/0/{size}/content", ct)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return (null, false);

        if (response.StatusCode == HttpStatusCode.BadRequest)
            return (null, true);

        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        return (new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false)), false);
    }
}
