namespace FileVault.Core;

public interface IFileProviderResolver
{
    Task<IFileProvider?> ResolveAsync(string route, CancellationToken ct = default);
    Task<IReadOnlyList<IDriveItem>> GetDrivesAsync(CancellationToken ct = default);
    Task<IFolderItem?> GetFolderAsync(string route, CancellationToken ct = default);
}
