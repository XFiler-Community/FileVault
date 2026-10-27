using FileVault.Core;

namespace FileVault.Dropbox;

/// <summary>A folder in Dropbox. Dropbox keeps no dates of folders: <see cref="ChangedDate"/> is <c>default</c>.</summary>
public sealed class DropboxFolderItem(DropboxClient client, DropboxEntry entry) : IFolderItem, IPublicLinkItem
{
    public DropboxClient Client => client;
    public DropboxEntry Entry => entry;
    public string Path => entry.Path;

    public string Name => entry.Name;
    public string FullName => DropboxPath.Format(entry.Path);
    public bool IsHidden => entry.Name.StartsWith('.');
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => default;
    public long? Size => null;

    public IFileProvider CreateProvider() => new DropboxFileProvider(client, entry.Path);

    public async Task<FileOperationResult<Uri>> GetPublicLinkAsync(CancellationToken ct = default)
    {
        try
        {
            return FileOperationResult<Uri>.Success(await client.GetPublicLinkAsync(entry.Path, ct).ConfigureAwait(false));
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
            await client.RemovePublicLinksAsync(entry.Path, ct).ConfigureAwait(false);
            return FileOperationResult<bool>.Success(true);
        }
        catch (Exception ex)
        {
            return FileOperationResult<bool>.Failure(ex);
        }
    }
}
