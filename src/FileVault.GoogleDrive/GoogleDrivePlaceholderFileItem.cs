using FileVault.Core;

namespace FileVault.GoogleDrive;

internal sealed class GoogleDrivePlaceholderFileItem(string destinationPath) : IFileItem
{
    // destinationPath is "gdrive:<parentId>/<fileName>"
    private string FileName => Path.GetFileName(destinationPath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public string Name => GetFileName();
    public string FullName => destinationPath;
    public bool IsHidden => false;
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => DateTimeOffset.Now;
    public long Size => 0;
    long? IFileProviderItem.Size => 0;
    public string Extension => Path.GetExtension(GetFileName());
    public string NameWithoutExtension => Path.GetFileNameWithoutExtension(GetFileName());

    public Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default)
        => Task.FromResult<(Stream, long)>((new MemoryStream(), 0));

    public Task DeleteAsync(CancellationToken ct = default) => Task.CompletedTask;

    private string GetFileName()
    {
        var slashIdx = destinationPath.LastIndexOf('/');
        return slashIdx < 0 ? destinationPath : destinationPath[(slashIdx + 1)..];
    }
}
