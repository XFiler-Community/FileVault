using FileVault.Core;

namespace FileVault.Local;

public sealed class SystemFileItem : IFileItem, IOpenableItem
{
    private readonly FileInfo _fileInfo;

    public SystemFileItem(FileInfo fileInfo)
    {
        _fileInfo = fileInfo;
        IsHidden = (fileInfo.Attributes & FileAttributes.Hidden) != 0;
        IsSystem = (fileInfo.Attributes & FileAttributes.System) != 0;
        ChangedDate = fileInfo.LastWriteTime;
        Size = fileInfo.Length;
    }

    internal SystemFileItem(string filePath)
    {
        _fileInfo = new FileInfo(filePath);
        IsHidden = false;
        IsSystem = false;
        ChangedDate = DateTimeOffset.Now;
        Size = 0;
    }

    public string Name => _fileInfo.Name;
    public string FullName => _fileInfo.FullName;
    public bool IsHidden { get; }
    public bool IsSystem { get; }
    public DateTimeOffset ChangedDate { get; }
    public long Size { get; }
    long? IFileProviderItem.Size => Size;

    public string Extension => _fileInfo.Extension;
    public string NameWithoutExtension => Path.GetFileNameWithoutExtension(_fileInfo.Name);

    public Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default)
    {
        var stream = _fileInfo.OpenRead();
        return Task.FromResult<(Stream, long)>((stream, _fileInfo.Length));
    }

    public Task DeleteAsync(CancellationToken ct = default)
    {
        _fileInfo.Delete();
        return Task.CompletedTask;
    }

    public void Open()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = _fileInfo.FullName,
            UseShellExecute = true
        });
    }
}
