using FileVault.Core;
using Google.Apis.Drive.v3;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace FileVault.GoogleDrive;

public sealed class GoogleDriveFileItem(DriveFile file, DriveService service) : IFileItem
{
    internal DriveFile File => file;

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
        var request = service.Files.Get(file.Id);
        await request.DownloadAsync(ms, ct).ConfigureAwait(false);
        ms.Position = 0;
        long totalBytes = file.Size ?? ms.Length;
        return (ms, totalBytes);
    }

    public async Task DeleteAsync(CancellationToken ct = default)
        => await service.Files.Delete(file.Id).ExecuteAsync(ct).ConfigureAwait(false);
}
