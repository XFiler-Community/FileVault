using FileVault.Core;

namespace FileVault.OneDrive;

/// <summary>
/// A file of a OneDrive. <see cref="IThumbnailItem.HasThumbnail"/> is a guess from the listing (pictures, videos,
/// Office documents and PDFs): Graph cannot list thumbnails with children on work/school drives, so a guessed picture
/// may still turn out missing — <see cref="OpenThumbnailAsync"/> then returns null.
/// </summary>
public sealed class OneDriveFileItem : IThumbnailItem, IPublicLinkItem
{
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf",
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".heic", ".heif", ".webp",
        ".mp4", ".mov", ".m4v", ".avi", ".wmv", ".mkv"
    };

    private readonly GraphDriveItem _item;

    internal OneDriveFileItem(OneDriveClient client, GraphDriveItem item)
    {
        Client = client;
        _item = item;
    }

    public OneDriveClient Client { get; }
    public string Id => _item.Id;

    /// <summary>The id of the folder holding the file, as listed.</summary>
    public string? ParentId => _item.ParentReference?.Id;

    public string? MimeType => _item.File?.MimeType;

    public string Name => _item.Name;
    public string FullName => OneDriveClient.FullNameOf(_item.Id);
    public bool IsHidden => _item.Name.StartsWith('.');
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => _item.LastModifiedDateTime ?? DateTimeOffset.Now;
    public long Size => _item.Size ?? 0;
    long? IFileProviderItem.Size => _item.Size;
    public string Extension => Path.GetExtension(_item.Name);
    public string NameWithoutExtension => Path.GetFileNameWithoutExtension(_item.Name);

    public Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default) =>
        Client.OpenReadAsync(_item.Id, ct);

    /// <summary>To the OneDrive recycle bin.</summary>
    public Task DeleteAsync(CancellationToken ct = default) => Client.MoveToRecycleBinAsync(_item.Id, ct);

    public bool HasThumbnail => _item.Image is not null || _item.Photo is not null || _item.Video is not null
                                || DocumentExtensions.Contains(Extension);

    public Task<Stream?> OpenThumbnailAsync(int size, CancellationToken ct = default) =>
        Client.OpenThumbnailAsync(_item.Id, size, ct);

    public async Task<FileOperationResult<Uri>> GetPublicLinkAsync(CancellationToken ct = default)
    {
        try
        {
            return FileOperationResult<Uri>.Success(await Client.GetPublicLinkAsync(_item.Id, ct).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            return FileOperationResult<Uri>.Failure(ex);
        }
    }

    public async Task<FileOperationResult<bool>> RemovePublicLinkAsync(CancellationToken ct = default)
    {
        try
        {
            await Client.RemovePublicLinksAsync(_item.Id, ct).ConfigureAwait(false);
            return FileOperationResult<bool>.Success(true);
        }
        catch (Exception ex)
        {
            return FileOperationResult<bool>.Failure(ex);
        }
    }
}
