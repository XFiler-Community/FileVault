using FileVault.Core;

namespace FileVault.OneDrive;

/// <summary>The drive root of the signed-in account with its quota (<c>GET /me/drive</c>).</summary>
public sealed class OneDriveDriveItem : IDriveItem
{
    private readonly GraphDrive _drive;

    internal OneDriveDriveItem(OneDriveClient client, GraphDrive drive)
    {
        Client = client;
        _drive = drive;
    }

    public OneDriveClient Client { get; }
    public string DriveId => _drive.Id;

    /// <summary><c>personal</c>, <c>business</c> or <c>documentLibrary</c>.</summary>
    public string? DriveType => _drive.DriveType;

    public string? OwnerName => _drive.Owner?.User?.DisplayName;

    public string Name => "OneDrive";
    public string FullName => OneDriveClient.FullNameOf(OneDriveClient.RootId);
    public bool IsHidden => false;
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => DateTimeOffset.Now;
    public long? Size => null;
    public long TotalSize => _drive.Quota?.Total ?? 0;
    public long TotalFreeSpace => _drive.Quota?.Remaining ?? Math.Max(0, TotalSize - (_drive.Quota?.Used ?? 0));
    public bool IsUserVisible => true;

    public IFileProvider CreateProvider() => new OneDriveFileProvider(Client);
}
