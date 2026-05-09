namespace FileVault.Core;

public sealed class CompositeFileProviderResolver(IEnumerable<IFileProviderResolver> resolvers) : IFileProviderResolver
{
    private readonly IReadOnlyList<IFileProviderResolver> _resolvers = resolvers.ToList();

    public async Task<IFileProvider?> ResolveAsync(string route, CancellationToken ct = default)
    {
        foreach (var resolver in _resolvers)
        {
            var provider = await resolver.ResolveAsync(route, ct).ConfigureAwait(false);
            if (provider is not null)
                return provider;
        }
        return null;
    }

    public async Task<IReadOnlyList<IDriveItem>> GetDrivesAsync(CancellationToken ct = default)
    {
        var result = new List<IDriveItem>();
        foreach (var resolver in _resolvers)
        {
            var drives = await resolver.GetDrivesAsync(ct).ConfigureAwait(false);
            result.AddRange(drives);
        }
        return result;
    }

    public async Task<IFolderItem?> GetFolderAsync(string route, CancellationToken ct = default)
    {
        foreach (var resolver in _resolvers)
        {
            var folder = await resolver.GetFolderAsync(route, ct).ConfigureAwait(false);
            if (folder is not null)
                return folder;
        }
        return null;
    }
}
