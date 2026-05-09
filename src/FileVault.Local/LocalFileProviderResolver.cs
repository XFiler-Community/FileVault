using FileVault.Core;

namespace FileVault.Local;

public sealed class LocalFileProviderResolver : IFileProviderResolver
{
    public Task<IFileProvider?> ResolveAsync(string route, CancellationToken ct = default)
    {
        if (!Path.IsPathRooted(route))
            return Task.FromResult<IFileProvider?>(null);

        if (!Directory.Exists(route))
            return Task.FromResult<IFileProvider?>(null);

        return Task.FromResult<IFileProvider?>(new LocalFileProvider(route));
    }

    public Task<IReadOnlyList<IDriveItem>> GetDrivesAsync(CancellationToken ct = default)
    {
        IReadOnlyList<IDriveItem> drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => (IDriveItem)new LocalDriveItem(d))
            .ToList();
        return Task.FromResult(drives);
    }

    public Task<IFolderItem?> GetFolderAsync(string route, CancellationToken ct = default)
    {
        if (!Path.IsPathRooted(route) || !Directory.Exists(route))
            return Task.FromResult<IFolderItem?>(null);

        return Task.FromResult<IFolderItem?>(new SystemFolderItem(new DirectoryInfo(route)));
    }
}
