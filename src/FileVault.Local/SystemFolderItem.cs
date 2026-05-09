using FileVault.Core;

namespace FileVault.Local;

public sealed class SystemFolderItem : IFolderItem, IOpenableItem
{
    private readonly DirectoryInfo _dirInfo;

    public SystemFolderItem(DirectoryInfo dirInfo)
    {
        _dirInfo = dirInfo;
        IsHidden = (dirInfo.Attributes & FileAttributes.Hidden) != 0;
        IsSystem = (dirInfo.Attributes & FileAttributes.System) != 0;
        ChangedDate = dirInfo.LastWriteTime;
    }

    public string Name => _dirInfo.Name;
    public string FullName => _dirInfo.FullName;
    public bool IsHidden { get; }
    public bool IsSystem { get; }
    public DateTimeOffset ChangedDate { get; }
    public long? Size => null;

    public IFileProvider CreateProvider() => new LocalFileProvider(_dirInfo.FullName);

    public void Open()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = _dirInfo.FullName,
            UseShellExecute = true
        });
    }
}
