using FileVault.Core;
using Google.Apis.Drive.v3;
using Google.Apis.Drive.v3.Data;

namespace FileVault.GoogleDrive;

/// <summary>
/// Google Drive "anyone with the link": a permission of type <c>anyone</c> lets the item's <c>webViewLink</c> open
/// without signing in.
/// </summary>
internal static class GoogleDrivePublicLink
{
    private const string Anyone = "anyone";

    public static async Task<FileOperationResult<Uri>> GetAsync(DriveService service, string fileId, CancellationToken ct)
    {
        try
        {
            var anyone = await ListAnyonePermissionsAsync(service, fileId, ct).ConfigureAwait(false);

            if (anyone.Count == 0)
            {
                var create = service.Permissions.Create(
                    new Permission { Type = Anyone, Role = "reader", AllowFileDiscovery = false }, fileId);
                create.Fields = "id";
                create.SupportsAllDrives = true;
                await create.ExecuteAsync(ct).ConfigureAwait(false);
            }

            var get = service.Files.Get(fileId);
            get.Fields = "webViewLink";
            get.SupportsAllDrives = true;
            var file = await get.ExecuteAsync(ct).ConfigureAwait(false);

            if (string.IsNullOrEmpty(file.WebViewLink))
                throw new InvalidOperationException($"Google Drive gave no link for '{fileId}'.");

            return FileOperationResult<Uri>.Success(new Uri(file.WebViewLink));
        }
        catch (Exception ex)
        {
            return FileOperationResult<Uri>.Failure(ex);
        }
    }

    /// <summary>True when a link was removed, false when the item had none.</summary>
    public static async Task<FileOperationResult<bool>> RemoveAsync(DriveService service, string fileId,
        CancellationToken ct)
    {
        try
        {
            var anyone = await ListAnyonePermissionsAsync(service, fileId, ct).ConfigureAwait(false);

            foreach (var permission in anyone)
            {
                var delete = service.Permissions.Delete(fileId, permission.Id);
                delete.SupportsAllDrives = true;
                await delete.ExecuteAsync(ct).ConfigureAwait(false);
            }

            return FileOperationResult<bool>.Success(anyone.Count > 0);
        }
        catch (Exception ex)
        {
            return FileOperationResult<bool>.Failure(ex);
        }
    }

    private static async Task<List<Permission>> ListAnyonePermissionsAsync(DriveService service, string fileId,
        CancellationToken ct)
    {
        var anyone = new List<Permission>();
        string? pageToken = null;

        do
        {
            var request = service.Permissions.List(fileId);
            request.Fields = "nextPageToken, permissions(id, type, role)";
            request.SupportsAllDrives = true;
            request.PageToken = pageToken;

            var page = await request.ExecuteAsync(ct).ConfigureAwait(false);
            anyone.AddRange((page.Permissions ?? []).Where(p => p.Type == Anyone));
            pageToken = page.NextPageToken;
        }
        while (pageToken != null);

        return anyone;
    }
}
