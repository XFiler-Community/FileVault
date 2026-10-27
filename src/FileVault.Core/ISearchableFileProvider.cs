namespace FileVault.Core;

/// <summary>
/// Optional: a provider that can find items by name below its folder on the backend's side (Yandex Disk, Google Drive,
/// OneDrive, Dropbox), so a client does not have to walk every subfolder. Check with
/// <c>provider is ISearchableFileProvider searchable</c>.
/// </summary>
public interface ISearchableFileProvider : IFileProvider
{
    /// <summary>
    /// Files and folders whose name contains <paramref name="query"/> (case-insensitive), in this provider's folder and
    /// every folder below it. Streams as pages arrive; the order is the backend's. Items carry full names a client can
    /// resolve like any other item of this provider. A backend whose index is fuzzier than "name contains" may return
    /// a few more; the caller filters if it needs an exact match.
    /// </summary>
    IAsyncEnumerable<IFileProviderItem> SearchAsync(string query, CancellationToken ct = default);
}
