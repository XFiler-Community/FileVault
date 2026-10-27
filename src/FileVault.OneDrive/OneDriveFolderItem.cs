using FileVault.Core;

namespace FileVault.OneDrive;

public sealed class OneDriveFolderItem : IFolderItem, IPublicLinkItem
{
    private readonly GraphDriveItem _item;

    internal OneDriveFolderItem(OneDriveClient client, GraphDriveItem item)
    {
        Client = client;
        _item = item;
    }

    public OneDriveClient Client { get; }
    public string Id => _item.Id;
    public string? ParentId => _item.ParentReference?.Id;
    public int? ChildCount => _item.Folder?.ChildCount;

    public string Name => _item.Name;
    public string FullName => OneDriveClient.FullNameOf(_item.Id);
    public bool IsHidden => _item.Name.StartsWith('.');
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => _item.LastModifiedDateTime ?? DateTimeOffset.Now;
    public long? Size => null;

    public IFileProvider CreateProvider() => new OneDriveFileProvider(Client, _item.Id, _item.Name);

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
