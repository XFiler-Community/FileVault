using FileVault.Core;

namespace FileVault.WebDav;

/// <summary>
/// A folder on a WebDAV server. Items of the same <see cref="WebDavConnection"/> are copied and moved by the server
/// (COPY / MOVE with <c>Overwrite: F</c>); uploads go to a temp name in the destination folder and are moved over the
/// destination only when complete. WebDAV has no trash: <c>DeleteAsync(toRecycleBin: true)</c> is refused.
/// </summary>
public sealed class WebDavFileProvider : IFileProvider
{
    private readonly WebDavConnection _connection;
    private readonly string _path;

    /// <param name="connection">The account.</param>
    /// <param name="path">
    /// A folder relative to <see cref="WebDavConnection.BaseUri"/> (<c>""</c> or <c>"/"</c> — the root,
    /// <c>"Photos/2024"</c>), or a <c>FullName</c> / http(s) URL of the same server below the base URI.
    /// </param>
    public WebDavFileProvider(WebDavConnection connection, string path = "")
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connection = connection;
        _path = connection.ResolvePath(path);
    }

    private WebDavFileProvider(string serverPath, WebDavConnection connection)
    {
        _connection = connection;
        _path = serverPath;
    }

    internal static WebDavFileProvider ForServerPath(WebDavConnection connection, string serverPath) =>
        new(serverPath, connection);

    public WebDavConnection Connection => _connection;

    /// <summary>The decoded path of the folder on the server, from the host root.</summary>
    public string ServerPath => _path;

    private WebDavClient Client => _connection.Client;

    public string Name => _path == _connection.RootPath ? _connection.Name : WebDavPath.GetName(_path);
    public string FullName => Client.FullName(_path);

    public async IAsyncEnumerable<IFileProviderItem> GetItemsAsync(FileProviderFilter filter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var resources = await Client.ListAsync(_path, ct).ConfigureAwait(false);
        var visible = resources
            .Where(r => filter.ShowHiddenItems || !WebDavPath.GetName(r.Path).StartsWith('.'))
            .OrderBy(r => WebDavPath.GetName(r.Path), StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var folder in visible.Where(r => r.IsCollection))
            yield return new WebDavFolderItem(_connection, folder.Path, folder.LastModified ?? DateTimeOffset.MinValue);

        foreach (var file in visible.Where(r => !r.IsCollection))
            yield return new WebDavFileItem(_connection, file.Path, file.Length ?? 0,
                file.LastModified ?? DateTimeOffset.MinValue);
    }

    public async Task<FileOperationResult<IFolderItem>> CreateFolderAsync(string name, CancellationToken ct = default)
    {
        try
        {
            WebDavPath.EnsureValidName(name);
            var resolved = await FindFreeNameAsync(name, isFile: false, ct).ConfigureAwait(false);
            var path = WebDavPath.Combine(_path, resolved);

            await Client.CreateFolderAsync(path, ct).ConfigureAwait(false);

            IFolderItem result = new WebDavPlaceholderFolderItem(_connection, path);
            return FileOperationResult<IFolderItem>.Success(result);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFolderItem>.Failure(ex);
        }
    }

    public async Task<FileOperationResult<IFileItem>> CreateEmptyTextFileAsync(string name, CancellationToken ct = default)
    {
        try
        {
            WebDavPath.EnsureValidName(name);
            var resolved = await FindFreeNameAsync(name, isFile: true, ct).ConfigureAwait(false);
            var path = WebDavPath.Combine(_path, resolved);

            await Client.PutAsync(path, Stream.Null, 0, null, mustBeNew: true, ct).ConfigureAwait(false);

            IFileItem result = new WebDavPlaceholderFileItem(_connection, path);
            return FileOperationResult<IFileItem>.Success(result);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFileItem>.Failure(ex);
        }
    }

    public async Task<FileOperationResult<IReadOnlyList<IFileProviderItem>>> DeleteAsync(
        IReadOnlyList<IFileProviderItem> items, bool toRecycleBin, CancellationToken ct = default)
    {
        try
        {
            if (toRecycleBin)
                throw new NotSupportedException("WebDAV has no recycle bin; delete permanently instead.");

            var targets = items.Select(item => (Path: OwnPath(item), IsFolder: item is IFolderItem)).ToList();

            foreach (var (path, isFolder) in targets)
            {
                ct.ThrowIfCancellationRequested();

                if (path == _connection.RootPath)
                    throw new IOException("The root folder of the connection cannot be deleted.");

                await Client.DeleteAsync(path, isFolder, ct).ConfigureAwait(false);
            }

            return FileOperationResult<IReadOnlyList<IFileProviderItem>>.Success(items);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IReadOnlyList<IFileProviderItem>>.Failure(ex);
        }
    }

    public async Task<FileOperationResult<bool>> RenameAsync(IFileProviderItem item, string newName,
        CancellationToken ct = default)
    {
        try
        {
            WebDavPath.EnsureValidName(newName);
            var from = OwnPath(item);
            var to = WebDavPath.Combine(WebDavPath.Parent(from), newName);

            if (from == to)
                return FileOperationResult<bool>.Success(true);

            if (!await Client.TransferAsync(move: true, from, to, item is IFolderItem, overwrite: false, ct)
                    .ConfigureAwait(false))
                throw new IOException($"'{newName}' already exists.");

            return FileOperationResult<bool>.Success(true);
        }
        catch (Exception ex)
        {
            return FileOperationResult<bool>.Failure(ex);
        }
    }

    public async Task<FileOperationResult<IFileItem>> CopyFileInAsync(IFileItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default)
    {
        try
        {
            var destination = ParseDestination(destinationPath);

            if (SameServerPath(sourceItem) is { } source)
            {
                if (!await Client.TransferAsync(move: false, source, destination, collection: false, overwrite: false, ct)
                        .ConfigureAwait(false))
                    await CopyOverAsync(source, destination, ct).ConfigureAwait(false);

                progress.Report(1.0);
            }
            else
            {
                (var src, long len) = await sourceItem.OpenReadAsync(ct).ConfigureAwait(false);
                await using (src)
                {
                    await UploadAsync(src, len, destination, progress, ct).ConfigureAwait(false);
                }
            }

            IFileItem result = new WebDavPlaceholderFileItem(_connection, destination);
            return FileOperationResult<IFileItem>.Success(result);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFileItem>.Failure(ex);
        }
    }

    public async Task<FileOperationResult<IFileItem>> MoveFileInAsync(IFileItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default)
    {
        try
        {
            var destination = ParseDestination(destinationPath);

            if (SameServerPath(sourceItem) is { } source)
            {
                if (source != destination)
                    await ReplaceWithAsync(source, destination, ct).ConfigureAwait(false);

                progress.Report(1.0);
            }
            else
            {
                var copy = await CopyFileInAsync(sourceItem, destinationPath, progress, ct).ConfigureAwait(false);

                if (!copy.IsSuccess)
                    return copy;

                await sourceItem.DeleteAsync(ct).ConfigureAwait(false);
            }

            IFileItem result = new WebDavPlaceholderFileItem(_connection, destination);
            return FileOperationResult<IFileItem>.Success(result);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFileItem>.Failure(ex);
        }
    }

    /// <summary>
    /// Same server — one COPY of the whole folder. When the destination folder already exists (or the item is from
    /// elsewhere) the result is null and the caller copies item by item, merging instead of replacing the folder.
    /// </summary>
    public async Task<FileOperationResult<IFolderItem?>> CopyFolderInAsync(IFolderItem sourceItem,
        string destinationPath, IProgress<double> progress, CancellationToken ct = default)
    {
        try
        {
            if (SameServerPath(sourceItem) is not { } source)
                return FileOperationResult<IFolderItem?>.Success(null);

            var destination = ParseDestination(destinationPath);
            EnsureNotInside(source, destination);

            if (!await Client.TransferAsync(move: false, source, destination, collection: true, overwrite: false, ct)
                    .ConfigureAwait(false))
                return FileOperationResult<IFolderItem?>.Success(null);

            progress.Report(1.0);
            IFolderItem? result = new WebDavPlaceholderFolderItem(_connection, destination);
            return FileOperationResult<IFolderItem?>.Success(result);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFolderItem?>.Failure(ex);
        }
    }

    /// <summary>Same server — one MOVE; an existing destination folder or a foreign item — null (merge by recursion).</summary>
    public async Task<FileOperationResult<IFolderItem?>> TryMoveFolderAsync(IFolderItem sourceItem,
        string destinationPath, IProgress<double> progress, CancellationToken ct = default)
    {
        try
        {
            if (SameServerPath(sourceItem) is not { } source)
                return FileOperationResult<IFolderItem?>.Success(null);

            var destination = ParseDestination(destinationPath);

            if (source != destination)
            {
                EnsureNotInside(source, destination);

                if (!await Client.TransferAsync(move: true, source, destination, collection: true, overwrite: false, ct)
                        .ConfigureAwait(false))
                    return FileOperationResult<IFolderItem?>.Success(null);
            }

            progress.Report(1.0);
            IFolderItem? result = new WebDavPlaceholderFolderItem(_connection, destination);
            return FileOperationResult<IFolderItem?>.Success(result);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFolderItem?>.Failure(ex);
        }
    }

    public async Task<bool> ContainsFileAsync(string fileName, CancellationToken ct = default) =>
        await Client.StatAsync(WebDavPath.Combine(_path, fileName), collection: false, quota: false, ct)
            .ConfigureAwait(false) is { IsCollection: false };

    public async Task<bool> ContainsFolderAsync(string folderName, CancellationToken ct = default) =>
        await Client.StatAsync(WebDavPath.Combine(_path, folderName), collection: false, quota: false, ct)
            .ConfigureAwait(false) is { IsCollection: true };

    public async Task<IFileItem> ResolveDestinationFileAsync(IFileItem sourceItem, bool overwrite,
        CancellationToken ct = default)
    {
        var name = overwrite
            ? sourceItem.Name
            : await FindFreeNameAsync(sourceItem.Name, isFile: true, ct).ConfigureAwait(false);
        return new WebDavPlaceholderFileItem(_connection, WebDavPath.Combine(_path, name));
    }

    public async Task<IFolderItem> ResolveDestinationFolderAsync(IFolderItem sourceItem, bool overwrite,
        CancellationToken ct = default)
    {
        var name = overwrite
            ? sourceItem.Name
            : await FindFreeNameAsync(sourceItem.Name, isFile: false, ct).ConfigureAwait(false);
        return new WebDavPlaceholderFolderItem(_connection, WebDavPath.Combine(_path, name));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The first of <c>name</c>, <c>name (2)</c>, … that no file or folder here has, from one listing. Taken names
    /// compare case-insensitively: on a case-insensitive server (IIS, Windows shares) a name differing only in case
    /// is the same item.
    /// </summary>
    private async Task<string> FindFreeNameAsync(string name, bool isFile, CancellationToken ct)
    {
        var taken = (await Client.ListAsync(_path, ct).ConfigureAwait(false))
            .Select(r => WebDavPath.GetName(r.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!taken.Contains(name))
            return name;

        var baseName = isFile ? Path.GetFileNameWithoutExtension(name) : name;
        var extension = isFile ? Path.GetExtension(name) : "";

        for (var index = 2; ; index++)
        {
            var candidate = $"{baseName} ({index}){extension}";

            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// Uploads to a temp file next to the destination and moves it over the destination only when complete: a
    /// cancelled or failed upload leaves neither a truncated file nor a damaged original (Replace) on servers that
    /// write PUT bodies in place.
    /// </summary>
    private async Task UploadAsync(Stream source, long totalBytes, string destination, IProgress<double> progress,
        CancellationToken ct)
    {
        var temp = GetPartialPath(destination);

        try
        {
            await Client.PutAsync(temp, source, totalBytes, progress, mustBeNew: false, ct).ConfigureAwait(false);
            await ReplaceWithAsync(temp, destination, ct).ConfigureAwait(false);
        }
        catch
        {
            await TryDeleteAsync(temp).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Server-side copy over an existing file, through a temp copy so a failure keeps the old content.</summary>
    private async Task CopyOverAsync(string source, string destination, CancellationToken ct)
    {
        var temp = GetPartialPath(destination);

        try
        {
            await Client.TransferAsync(move: false, source, temp, collection: false, overwrite: true, ct)
                .ConfigureAwait(false);
            await ReplaceWithAsync(temp, destination, ct).ConfigureAwait(false);
        }
        catch
        {
            await TryDeleteAsync(temp).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// MOVE a file to the destination, replacing a file there; a folder there is never replaced (Overwrite: T
    /// would delete it with everything in it).
    /// </summary>
    private async Task ReplaceWithAsync(string source, string destination, CancellationToken ct)
    {
        if (await Client.TransferAsync(move: true, source, destination, collection: false, overwrite: false, ct)
                .ConfigureAwait(false))
            return;

        if (await Client.StatAsync(destination, collection: false, quota: false, ct).ConfigureAwait(false)
            is { IsCollection: true })
            throw new IOException($"'{Client.FullName(destination)}' is a folder; a file cannot replace it.");

        await Client.TransferAsync(move: true, source, destination, collection: false, overwrite: true, ct)
            .ConfigureAwait(false);
    }

    private async Task TryDeleteAsync(string path)
    {
        // The operation token is usually already cancelled here; cleanup gets its own short deadline.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            await Client.DeleteAsync(path, collection: false, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // The original error is what the caller needs; a leftover temp file is the lesser problem.
        }
    }

    private static string GetPartialPath(string destination)
    {
        var name = WebDavPath.GetName(destination);
        // Keep the temp name within the common 255-char file name limit.
        var prefix = name.Length <= 200 ? name : string.Empty;
        return WebDavPath.Combine(WebDavPath.Parent(destination), $"{prefix}.filevault-partial-{Guid.NewGuid():N}");
    }

    /// <summary>
    /// The server path of an item of this same connection — the only case the server can copy or move by itself.
    /// Comparing the connection instance (not the item type) keeps a copy from another account on the same host
    /// from running with these credentials.
    /// </summary>
    private string? SameServerPath(IFileProviderItem item) => item switch
    {
        WebDavFileItem file when ReferenceEquals(file.Connection, _connection) => file.ServerPath,
        WebDavFolderItem folder when ReferenceEquals(folder.Connection, _connection) => folder.ServerPath,
        WebDavPlaceholderFileItem file when ReferenceEquals(file.Connection, _connection) => file.ServerPath,
        WebDavPlaceholderFolderItem folder when ReferenceEquals(folder.Connection, _connection) => folder.ServerPath,
        WebDavDriveItem drive when ReferenceEquals(drive.Connection, _connection) => _connection.RootPath,
        _ => null
    };

    /// <summary>
    /// An item to rename or delete here: one of this connection, or one whose FullName is on this server below the
    /// base URI. A bare path is refused: a local item's <c>/home/…</c> must not name a file on the server.
    /// </summary>
    private string OwnPath(IFileProviderItem item) =>
        SameServerPath(item)
        ?? (item.FullName.Contains("://", StringComparison.Ordinal)
            ? _connection.ResolvePath(item.FullName)
            : throw new ArgumentException($"'{item.FullName}' is not an item of {FullName}.", nameof(item)));

    private string ParseDestination(string destinationPath)
    {
        var destination = _connection.ResolvePath(destinationPath);

        if (destination == _connection.RootPath)
            throw new ArgumentException($"'{destinationPath}' is the root folder, not a destination for an item.",
                nameof(destinationPath));

        return destination;
    }

    private static void EnsureNotInside(string source, string destination)
    {
        if (WebDavPath.IsSameOrInside(destination, source) && destination != source)
            throw new IOException($"Cannot put '{source}' inside itself.");
    }
}
