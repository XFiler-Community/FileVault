using FileVault.Core;
using Google.Apis.Drive.v3;

namespace FileVault.GoogleDrive;

/// <summary>
/// Resolves Google Drive routes. Accepts a pre-authenticated <see cref="DriveService"/>;
/// the caller is responsible for OAuth2 / service account authentication.
/// </summary>
public sealed class GoogleDriveFileProviderResolver(DriveService service) : IFileProviderResolver
{
    private const string RootRoute = "x-filevault:google-drive";
    private const string Scheme = "gdrive:";
    private const string DriveRoot = "root";

    public Task<IFileProvider?> ResolveAsync(string route, CancellationToken ct = default)
    {
        if (route == RootRoute || route.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            var folderId = route == RootRoute ? DriveRoot : ExtractFolderId(route);
            return Task.FromResult<IFileProvider?>(new GoogleDriveFileProvider(service, folderId));
        }

        return Task.FromResult<IFileProvider?>(null);
    }

    public async Task<IReadOnlyList<IDriveItem>> GetDrivesAsync(CancellationToken ct = default)
    {
        var aboutRequest = service.About.Get();
        aboutRequest.Fields = "storageQuota";
        var about = await aboutRequest.ExecuteAsync(ct).ConfigureAwait(false);

        var quota = about.StorageQuota;
        long total = quota?.Limit ?? 0;
        long used = (quota?.UsageInDrive ?? 0) + (quota?.UsageInDriveTrash ?? 0);

        return [new GoogleDriveDriveItem(service, total, used)];
    }

    public async Task<IFolderItem?> GetFolderAsync(string route, CancellationToken ct = default)
    {
        if (route != RootRoute && !route.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            return null;

        var folderId = route == RootRoute ? DriveRoot : ExtractFolderId(route);

        try
        {
            var request = service.Files.Get(folderId);
            request.Fields = "id, name, mimeType, modifiedTime, parents";
            var file = await request.ExecuteAsync(ct).ConfigureAwait(false);

            if (file.MimeType == "application/vnd.google-apps.folder" || folderId == DriveRoot)
                return new GoogleDriveFolderItem(file, service);
        }
        catch { }

        return null;
    }

    // Extracts the folder ID from "gdrive:<folderId>" or "gdrive:<parentId>/<name>".
    // For path-like routes, returns the first segment (the parent ID).
    private static string ExtractFolderId(string route)
    {
        var s = route[Scheme.Length..];
        var slash = s.IndexOf('/');
        return slash < 0 ? s : s[..slash];
    }
}
