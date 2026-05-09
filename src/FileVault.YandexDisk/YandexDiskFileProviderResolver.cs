using Egorozh.YandexDisk.Client.Http;
using Egorozh.YandexDisk.Client.Protocol;
using FileVault.Core;

namespace FileVault.YandexDisk;

public sealed class YandexDiskFileProviderResolver(string oauthToken) : IFileProviderResolver
{
    private const string YandexRoute = "x-filevault:yandex-disk";
    private const string DiskRoot = "disk:/";

    private readonly DiskHttpApi _api = new(oauthToken, logSaver: null);

    public Task<IFileProvider?> ResolveAsync(string route, CancellationToken ct = default)
    {
        if (route == YandexRoute || route.StartsWith("disk:/", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<IFileProvider?>(new YandexDiskFileProvider(_api, route == YandexRoute ? DiskRoot : route));

        return Task.FromResult<IFileProvider?>(null);
    }

    public async Task<IReadOnlyList<IDriveItem>> GetDrivesAsync(CancellationToken ct = default)
    {
        var disk = await _api.MetaInfo.GetDiskInfoAsync(ct).ConfigureAwait(false);
        return [new YandexDiskDriveItem(_api, disk.TotalSpace, disk.UsedSpace)];
    }

    public async Task<IFolderItem?> GetFolderAsync(string route, CancellationToken ct = default)
    {
        if (route != YandexRoute && !route.StartsWith("disk:/", StringComparison.OrdinalIgnoreCase))
            return null;

        var path = route == YandexRoute ? DiskRoot : route;
        try
        {
            var resource = await _api.MetaInfo.GetInfoAsync(new ResourceRequest { Path = path }, ct).ConfigureAwait(false);
            if (resource.Type == ResourceType.Dir)
                return new YandexDiskFolderItem(resource, _api);
        }
        catch { }
        return null;
    }
}
