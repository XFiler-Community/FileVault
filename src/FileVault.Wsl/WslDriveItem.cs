using System.Diagnostics.CodeAnalysis;
using FileVault.Core;
using FileVault.Local;

namespace FileVault.Wsl;

public sealed class WslDriveItem : IDriveItem
{
    private const string UncRoot = @"\\wsl$\";

    private readonly string _uncPath;

    public WslDriveItem(string distroName)
    {
        Name = distroName;
        _uncPath = UncRoot + distroName + @"\";
        FullName = _uncPath;

        try
        {
            ChangedDate = Directory.GetLastWriteTime(_uncPath);
        }
        catch
        {
            ChangedDate = DateTimeOffset.Now;
        }
    }

    public string Name { get; }
    public string FullName { get; }
    public bool IsHidden => false;
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate { get; }
    public long? Size => null;

    // DriveInfo doesn't support WSL UNC paths — disk usage unavailable
    public long TotalSize => 0;
    public long TotalFreeSpace => 0;
    public bool IsUserVisible => true;

    public IFileProvider CreateProvider() => new LocalFileProvider(_uncPath);
}
