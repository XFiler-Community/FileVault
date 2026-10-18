using FileVault.Core;
using Google.Apis.Download;
using Google.Apis.Drive.v3;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace FileVault.GoogleDrive;

public sealed class GoogleDriveFileItem(DriveFile file, DriveService service) : IFileItem
{
    internal DriveFile File => file;
    internal DriveService Service => service;

    public string Name => file.Name;
    public string FullName => $"gdrive:{file.Id}";
    public bool IsHidden => file.Name.StartsWith('.');
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => file.ModifiedTimeDateTimeOffset ?? DateTimeOffset.Now;
    public long Size => file.Size ?? 0;
    long? IFileProviderItem.Size => file.Size;
    public string Extension => Path.GetExtension(file.Name);
    public string NameWithoutExtension => Path.GetFileNameWithoutExtension(file.Name);

    public async Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default)
    {
        var ms = new MemoryStream();
        try
        {
            var download = GoogleDriveMimeTypes.IsWorkspaceType(file.MimeType)
                ? await ExportAsync(ms, ct).ConfigureAwait(false)
                : await service.Files.Get(file.Id).DownloadAsync(ms, ct).ConfigureAwait(false);
            GoogleDriveTransfer.EnsureCompleted(download);
        }
        catch
        {
            await ms.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        ms.Position = 0;
        return (ms, ms.Length);
    }

    private Task<IDownloadProgress> ExportAsync(Stream destination, CancellationToken ct)
    {
        var exportMimeType = GoogleDriveMimeTypes.GetExportMimeType(file.MimeType)
            ?? throw new NotSupportedException(
                $"Google Drive item '{file.Name}' ({file.MimeType}) has no downloadable content.");
        return service.Files.Export(file.Id, exportMimeType).DownloadAsync(destination, ct);
    }

    public async Task DeleteAsync(CancellationToken ct = default)
        => await service.Files.Delete(file.Id).ExecuteAsync(ct).ConfigureAwait(false);
}
