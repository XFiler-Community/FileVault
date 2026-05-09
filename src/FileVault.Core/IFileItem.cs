namespace FileVault.Core;

public interface IFileItem : IFileProviderItem
{
    string Extension { get; }
    string NameWithoutExtension { get; }
    new long Size { get; }

    Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default);
    Task DeleteAsync(CancellationToken ct = default);
}
