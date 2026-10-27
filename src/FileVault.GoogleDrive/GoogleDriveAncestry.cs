using System.Net;
using Google;
using Google.Apis.Drive.v3;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace FileVault.GoogleDrive;

/// <summary>
/// Tells whether a Drive item lies below one folder by walking its parents up. Each folder's answer is kept, so a
/// search reads every ancestor folder at most once however many items share it.
/// </summary>
internal sealed class GoogleDriveAncestry(DriveService service, string folderId)
{
    private readonly Dictionary<string, bool> _below = new(StringComparer.Ordinal) { [folderId] = true };

    public async Task<bool> IsBelowAsync(DriveFile file, CancellationToken ct)
        => file.Id != folderId && await AnyBelowAsync(file.Parents, ct).ConfigureAwait(false);

    private async Task<bool> AnyBelowAsync(IList<string>? parents, CancellationToken ct)
    {
        foreach (var parent in parents ?? [])
        {
            if (await IsFolderOrBelowAsync(parent, ct).ConfigureAwait(false))
                return true;
        }

        return false;
    }

    private async Task<bool> IsFolderOrBelowAsync(string id, CancellationToken ct)
    {
        if (_below.TryGetValue(id, out var known))
            return known;

        // Set before the walk so a parent loop (Drive should have none) ends instead of recursing forever.
        _below[id] = false;

        var parents = await GetParentsAsync(id, ct).ConfigureAwait(false);
        var below = await AnyBelowAsync(parents, ct).ConfigureAwait(false);
        _below[id] = below;
        return below;
    }

    // A folder the user cannot see (the parent of an item shared with them) cannot lie below their folder.
    private async Task<IList<string>?> GetParentsAsync(string id, CancellationToken ct)
    {
        try
        {
            var request = service.Files.Get(id);
            request.Fields = "id, parents";
            var folder = await request.ExecuteAsync(ct).ConfigureAwait(false);
            return folder.Parents;
        }
        catch (GoogleApiException ex) when (ex.HttpStatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return null;
        }
    }
}
