using FileVault.Core;
using Google.Apis.Drive.v3;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace FileVault.GoogleDrive;

public sealed class GoogleDriveFileProvider(DriveService service, string folderId) : IFileProvider
{
    private const string FolderMimeType = "application/vnd.google-apps.folder";
    private const string Scheme = "gdrive:";
    private const string ListFields = "nextPageToken, files(id, name, mimeType, size, modifiedTime, parents)";

    public string Name => folderId == "root" ? "Google Drive" : folderId;
    public string FullName => $"{Scheme}{folderId}";

    public async IAsyncEnumerable<IFileProviderItem> GetItemsAsync(FileProviderFilter filter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        string? pageToken = null;
        do
        {
            var request = service.Files.List();
            request.Q = $"'{EscapeQuery(folderId)}' in parents and trashed = false";
            request.Fields = ListFields;
            request.PageSize = 100;
            request.PageToken = pageToken;

            var result = await request.ExecuteAsync(ct).ConfigureAwait(false);

            foreach (var file in result.Files ?? [])
            {
                if (!filter.ShowHiddenItems && file.Name.StartsWith('.'))
                    continue;

                if (file.MimeType == FolderMimeType)
                    yield return new GoogleDriveFolderItem(file, service);
                else
                    yield return new GoogleDriveFileItem(file, service);
            }

            pageToken = result.NextPageToken;
        }
        while (pageToken != null);
    }

    public async Task<FileOperationResult<IFolderItem>> CreateFolderAsync(string name, CancellationToken ct = default)
    {
        try
        {
            var resolvedName = await ResolveFolderNameAsync(name, ct).ConfigureAwait(false);
            var metadata = new DriveFile
            {
                Name = resolvedName,
                MimeType = FolderMimeType,
                Parents = [folderId]
            };
            var request = service.Files.Create(metadata);
            request.Fields = "id, name, modifiedTime, parents";
            var created = await request.ExecuteAsync(ct).ConfigureAwait(false);
            IFolderItem result = new GoogleDriveFolderItem(created, service);
            return FileOperationResult<IFolderItem>.Success(result);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFolderItem>.Failure(ex);
        }
    }

    public async Task<FileOperationResult<IFileItem>> CreateEmptyTextFileAsync(string name, CancellationToken ct = default)
    {
        try
        {
            var metadata = new DriveFile { Name = name, Parents = [folderId] };
            using var empty = new MemoryStream();
            var request = service.Files.Create(metadata, empty, "text/plain");
            request.Fields = "id, name, size, modifiedTime, parents";
            await request.UploadAsync(ct).ConfigureAwait(false);
            IFileItem result = request.ResponseBody is { } f
                ? new GoogleDriveFileItem(f, service)
                : new GoogleDrivePlaceholderFileItem(BuildPath(name));
            return FileOperationResult<IFileItem>.Success(result);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFileItem>.Failure(ex);
        }
    }

    public async Task<FileOperationResult<IReadOnlyList<IFileProviderItem>>> DeleteAsync(
        IReadOnlyList<IFileProviderItem> items, bool toRecycleBin, CancellationToken ct = default)
    {
        try
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var fileId = GetItemId(item);
                if (toRecycleBin)
                {
                    await service.Files.Update(new DriveFile { Trashed = true }, fileId)
                        .ExecuteAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    await service.Files.Delete(fileId).ExecuteAsync(ct).ConfigureAwait(false);
                }
            }
            return FileOperationResult<IReadOnlyList<IFileProviderItem>>.Success(items);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IReadOnlyList<IFileProviderItem>>.Failure(ex);
        }
    }

    public async Task<FileOperationResult<bool>> RenameAsync(IFileProviderItem item, string newName, CancellationToken ct = default)
    {
        try
        {
            var fileId = GetItemId(item);
            await service.Files.Update(new DriveFile { Name = newName }, fileId)
                .ExecuteAsync(ct).ConfigureAwait(false);
            return FileOperationResult<bool>.Success(true);
        }
        catch (Exception ex)
        {
            return FileOperationResult<bool>.Failure(ex);
        }
    }

    public async Task<FileOperationResult<IFileItem>> CopyFileInAsync(IFileItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default)
    {
        try
        {
            var (destParentId, destFileName) = ParsePath(destinationPath);

            if (sourceItem is GoogleDriveFileItem gdFile)
            {
                // Server-side copy within Google Drive
                var copyBody = new DriveFile { Name = destFileName, Parents = [destParentId] };
                var copyRequest = service.Files.Copy(copyBody, gdFile.File.Id);
                copyRequest.Fields = "id, name, size, modifiedTime, parents";
                var copied = await copyRequest.ExecuteAsync(ct).ConfigureAwait(false);
                progress.Report(1.0);
                IFileItem result = new GoogleDriveFileItem(copied, service);
                return FileOperationResult<IFileItem>.Success(result);
            }
            else
            {
                (var src, long len) = await sourceItem.OpenReadAsync(ct).ConfigureAwait(false);
                await using (src)
                {
                    var metadata = new DriveFile { Name = destFileName, Parents = [destParentId] };
                    var uploadRequest = service.Files.Create(metadata, src, "application/octet-stream");
                    uploadRequest.Fields = "id, name, size, modifiedTime, parents";
                    if (len > 0)
                        uploadRequest.ProgressChanged += p => progress.Report((double)p.BytesSent / len);
                    await uploadRequest.UploadAsync(ct).ConfigureAwait(false);
                    progress.Report(1.0);
                    IFileItem result = uploadRequest.ResponseBody is { } f
                        ? new GoogleDriveFileItem(f, service)
                        : new GoogleDrivePlaceholderFileItem(destinationPath);
                    return FileOperationResult<IFileItem>.Success(result);
                }
            }
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFileItem>.Failure(ex);
        }
    }

    public async Task<FileOperationResult<IFileItem>> MoveFileInAsync(IFileItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default)
    {
        try
        {
            var (destParentId, destFileName) = ParsePath(destinationPath);

            if (sourceItem is GoogleDriveFileItem gdFile)
            {
                // Server-side move: update parents + rename in one request
                var oldParents = string.Join(",", gdFile.File.Parents ?? []);
                var updateRequest = service.Files.Update(new DriveFile { Name = destFileName }, gdFile.File.Id);
                updateRequest.AddParents = destParentId;
                if (!string.IsNullOrEmpty(oldParents))
                    updateRequest.RemoveParents = oldParents;
                updateRequest.Fields = "id, name, size, modifiedTime, parents";
                var updated = await updateRequest.ExecuteAsync(ct).ConfigureAwait(false);
                progress.Report(1.0);
                IFileItem result = new GoogleDriveFileItem(updated, service);
                return FileOperationResult<IFileItem>.Success(result);
            }
            else
            {
                var copyResult = await CopyFileInAsync(sourceItem, destinationPath, progress, ct).ConfigureAwait(false);
                if (!copyResult.IsSuccess)
                    return copyResult;
                await sourceItem.DeleteAsync(ct).ConfigureAwait(false);
                return copyResult;
            }
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFileItem>.Failure(ex);
        }
    }

    // Google Drive API v3 has no server-side folder copy — signal orchestrator to recurse.
    public Task<FileOperationResult<IFolderItem?>> CopyFolderInAsync(IFolderItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default)
        => Task.FromResult(FileOperationResult<IFolderItem?>.Success(null));

    public async Task<FileOperationResult<IFolderItem?>> TryMoveFolderAsync(IFolderItem sourceItem,
        string destinationPath, IProgress<double> progress, CancellationToken ct = default)
    {
        if (sourceItem is not GoogleDriveFolderItem gdFolder)
            return FileOperationResult<IFolderItem?>.Success(null);

        try
        {
            var (destParentId, newFolderName) = ParsePath(destinationPath);
            var oldParents = string.Join(",", gdFolder.File.Parents ?? []);
            var updateRequest = service.Files.Update(new DriveFile { Name = newFolderName }, gdFolder.File.Id);
            updateRequest.AddParents = destParentId;
            if (!string.IsNullOrEmpty(oldParents))
                updateRequest.RemoveParents = oldParents;
            await updateRequest.ExecuteAsync(ct).ConfigureAwait(false);
            progress.Report(1.0);
            IFolderItem? result = new GoogleDrivePlaceholderFolderItem(destParentId, newFolderName, service);
            return FileOperationResult<IFolderItem?>.Success(result);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFolderItem?>.Failure(ex);
        }
    }

    public async Task<bool> ContainsFileAsync(string fileName, CancellationToken ct = default)
    {
        var q = $"'{EscapeQuery(folderId)}' in parents and name = '{EscapeQuery(fileName)}' and mimeType != '{FolderMimeType}' and trashed = false";
        var request = service.Files.List();
        request.Q = q;
        request.Fields = "files(id)";
        request.PageSize = 1;
        var result = await request.ExecuteAsync(ct).ConfigureAwait(false);
        return result.Files?.Count > 0;
    }

    public async Task<bool> ContainsFolderAsync(string folderName, CancellationToken ct = default)
    {
        var q = $"'{EscapeQuery(folderId)}' in parents and name = '{EscapeQuery(folderName)}' and mimeType = '{FolderMimeType}' and trashed = false";
        var request = service.Files.List();
        request.Q = q;
        request.Fields = "files(id)";
        request.PageSize = 1;
        var result = await request.ExecuteAsync(ct).ConfigureAwait(false);
        return result.Files?.Count > 0;
    }

    public async Task<IFileItem> ResolveDestinationFileAsync(IFileItem sourceItem, bool overwrite, CancellationToken ct = default)
    {
        if (overwrite)
            return new GoogleDrivePlaceholderFileItem(BuildPath(sourceItem.Name));

        var name = await FindUniqueFileNameAsync(sourceItem.Name, ct).ConfigureAwait(false);
        return new GoogleDrivePlaceholderFileItem(BuildPath(name));
    }

    public async Task<IFolderItem> ResolveDestinationFolderAsync(IFolderItem sourceItem, bool overwrite, CancellationToken ct = default)
    {
        if (overwrite)
            return new GoogleDrivePlaceholderFolderItem(folderId, sourceItem.Name, service);

        var name = await FindUniqueFolderNameAsync(sourceItem.Name, ct).ConfigureAwait(false);
        return new GoogleDrivePlaceholderFolderItem(folderId, name, service);
    }

    private async Task<string> FindUniqueFileNameAsync(string fileName, CancellationToken ct)
    {
        if (!await ContainsFileAsync(fileName, ct).ConfigureAwait(false))
            return fileName;

        var ext = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var index = 2;
        string candidate;
        do
        {
            candidate = $"{baseName} ({index}){ext}";
            index++;
        }
        while (await ContainsFileAsync(candidate, ct).ConfigureAwait(false));
        return candidate;
    }

    private async Task<string> FindUniqueFolderNameAsync(string folderName, CancellationToken ct)
    {
        if (!await ContainsFolderAsync(folderName, ct).ConfigureAwait(false))
            return folderName;

        var index = 2;
        string candidate;
        do
        {
            candidate = $"{folderName} ({index})";
            index++;
        }
        while (await ContainsFolderAsync(candidate, ct).ConfigureAwait(false));
        return candidate;
    }

    private async Task<string> ResolveFolderNameAsync(string name, CancellationToken ct)
    {
        if (!await ContainsFolderAsync(name, ct).ConfigureAwait(false))
            return name;

        var index = 2;
        string candidate;
        do
        {
            candidate = $"{name} ({index})";
            index++;
        }
        while (await ContainsFolderAsync(candidate, ct).ConfigureAwait(false));
        return candidate;
    }

    // Builds "gdrive:<folderId>/<name>" — used as FullName for placeholder items.
    private string BuildPath(string name) => $"{Scheme}{folderId}/{name}";

    // Parses "gdrive:<parentId>/<fileName>" → (parentId, fileName).
    private (string parentId, string name) ParsePath(string path)
    {
        var s = path.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) ? path[Scheme.Length..] : path;
        var slash = s.IndexOf('/');
        return slash < 0 ? (folderId, s) : (s[..slash], s[(slash + 1)..]);
    }

    private static string GetItemId(IFileProviderItem item) => item switch
    {
        GoogleDriveFileItem f => f.File.Id,
        GoogleDriveFolderItem d => d.File.Id,
        _ => StripScheme(item.FullName)
    };

    private static string StripScheme(string fullName)
    {
        var s = fullName.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) ? fullName[Scheme.Length..] : fullName;
        var slash = s.IndexOf('/');
        return slash < 0 ? s : s[..slash];
    }

    // Escapes single quotes in Drive query strings.
    private static string EscapeQuery(string value) => value.Replace(@"\", @"\\").Replace("'", @"\'");
}
