using FileVault.Core;

namespace FileVault.S3;

/// <summary>
/// A folder of an S3 bucket. Folders are common prefixes of keys; a new folder is a zero-byte <c>name/</c> marker
/// object. S3 has no rename or move, so both are a server-side copy and a delete — for a folder, of every key under
/// it. There is no trash: a delete to the recycle bin is refused.
/// </summary>
public sealed class S3FileProvider : ISearchableFileProvider
{
    private readonly S3Connection _connection;
    private readonly string _path;

    /// <param name="connection">The bucket and the keys to it.</param>
    /// <param name="prefix">The folder from the bucket root (<c>photos/2024</c>); empty — the bucket root.</param>
    public S3FileProvider(S3Connection connection, string prefix = "")
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(prefix);

        _connection = connection;
        _path = S3Path.Normalize(prefix);
    }

    public S3Connection Connection => _connection;

    /// <summary>The folder's path from the bucket root, without slashes at either end; empty — the root.</summary>
    public string Path => _path;

    public string Name => _path.Length == 0 ? _connection.Bucket : S3Path.GetName(_path);
    public string FullName => _connection.FullName(_path);

    private S3Client Client => _connection.Client;

    /// <summary>Folders stream as pages arrive; files follow once the listing is complete.</summary>
    public async IAsyncEnumerable<IFileProviderItem> GetItemsAsync(FileProviderFilter filter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var prefix = S3Path.Prefix(_path);
        var files = new List<S3FileItem>();
        string? token = null;

        do
        {
            var page = await Client.ListAsync(prefix, "/", token, null, ct).ConfigureAwait(false);

            foreach (var folderPrefix in page.CommonPrefixes)
            {
                if (!folderPrefix.StartsWith(prefix, StringComparison.Ordinal) || folderPrefix.Length <= prefix.Length + 1)
                    continue;

                var folder = new S3FolderItem(_connection, folderPrefix[..^1], DateTimeOffset.UnixEpoch);

                if (filter.ShowHiddenItems || !folder.IsHidden)
                    yield return folder;
            }

            // A key ending in '/' here can only be this folder's own marker
            foreach (var entry in page.Objects)
            {
                if (entry.Key.Length <= prefix.Length || entry.Key.EndsWith('/')
                    || !entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                var file = new S3FileItem(_connection, entry.Key, entry.Size, entry.LastModified, entry.ETag);

                if (filter.ShowHiddenItems || !file.IsHidden)
                    files.Add(file);
            }

            token = page.NextContinuationToken;
        } while (token is not null);

        foreach (var file in files)
            yield return file;
    }

    /// <summary>
    /// Every file and folder below this one whose name contains <paramref name="query"/>, ignoring case. One listing
    /// of all keys under the prefix: folders are the prefixes the keys imply, each yielded once.
    /// </summary>
    public async IAsyncEnumerable<IFileProviderItem> SearchAsync(string query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var prefix = S3Path.Prefix(_path);
        var folders = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var entry in Client.ListAllAsync(prefix, ct).ConfigureAwait(false))
        {
            if (!entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            var relative = entry.Key[prefix.Length..];
            var segmentStart = 0;

            for (var slash = relative.IndexOf('/'); slash >= 0; slash = relative.IndexOf('/', slash + 1))
            {
                var name = relative[segmentStart..slash];
                var folderPath = relative[..slash];
                segmentStart = slash + 1;

                if (name.Length > 0 && Matches(name, query) && folders.Add(folderPath))
                    yield return new S3FolderItem(_connection, S3Path.Combine(_path, folderPath), DateTimeOffset.UnixEpoch);
            }

            var fileName = relative[segmentStart..];

            if (fileName.Length > 0 && Matches(fileName, query))
                yield return new S3FileItem(_connection, entry.Key, entry.Size, entry.LastModified, entry.ETag);
        }
    }

    public async Task<FileOperationResult<IFolderItem>> CreateFolderAsync(string name, CancellationToken ct = default)
    {
        try
        {
            S3Path.EnsureValidName(name);
            var resolved = await ResolveNameAsync(name, name, n => $"{name} ({n})", ct).ConfigureAwait(false);
            var folder = S3Path.Combine(_path, resolved);

            await Client.PutAsync(S3Path.Prefix(folder), ReadOnlyMemory<byte>.Empty, null, ct).ConfigureAwait(false);

            return FileOperationResult<IFolderItem>.Success(new S3FolderItem(_connection, folder, DateTimeOffset.Now));
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFolderItem>.Failure(ex);
        }
    }

    public async Task<FileOperationResult<IFileItem>> CreateEmptyTextFileAsync(string name,
        CancellationToken ct = default)
    {
        try
        {
            S3Path.EnsureValidName(name);
            var resolved = await ResolveFileNameAsync(name, ct).ConfigureAwait(false);
            var key = S3Path.Combine(_path, resolved);

            await Client.PutAsync(key, ReadOnlyMemory<byte>.Empty, "text/plain", ct).ConfigureAwait(false);

            return FileOperationResult<IFileItem>.Success(new S3FileItem(_connection, key, 0, DateTimeOffset.Now, null));
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFileItem>.Failure(ex);
        }
    }

    /// <summary>
    /// S3 has no trash: <paramref name="toRecycleBin"/> fails with <see cref="NotSupportedException"/> and deletes
    /// nothing. A folder is deleted with every key under it.
    /// </summary>
    public async Task<FileOperationResult<IReadOnlyList<IFileProviderItem>>> DeleteAsync(
        IReadOnlyList<IFileProviderItem> items, bool toRecycleBin, CancellationToken ct = default)
    {
        try
        {
            if (toRecycleBin)
                return FileOperationResult<IReadOnlyList<IFileProviderItem>>.Failure(
                    new NotSupportedException("S3 storage has no recycle bin; items can only be deleted permanently."));

            var targets = items.Select(OwnTarget).ToList();
            var keys = new List<string>();

            foreach (var (path, isFolder) in targets)
            {
                if (!isFolder)
                {
                    keys.Add(path);
                    continue;
                }

                await foreach (var entry in Client.ListAllAsync(S3Path.Prefix(path), ct).ConfigureAwait(false))
                    keys.Add(entry.Key);
            }

            if (keys.Count > 0)
                await Client.DeleteManyAsync(keys.Distinct(StringComparer.Ordinal).ToList(), ct).ConfigureAwait(false);

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
            S3Path.EnsureValidName(newName);
            var (from, isFolder) = OwnTarget(item);
            var to = S3Path.Combine(S3Path.Parent(from), newName);

            if (to == from)
                return FileOperationResult<bool>.Success(true);

            if (await ExistsAsync(to, ct).ConfigureAwait(false))
                throw new IOException($"'{newName}' already exists.");

            if (isFolder)
            {
                await MovePrefixAsync(_connection, from, to, null, ct).ConfigureAwait(false);
            }
            else
            {
                var size = item is S3FileItem file ? file.Size : await SizeOfAsync(from, ct).ConfigureAwait(false);
                await Client.CopyObjectAsync(_connection, from, size, to, null, ct).ConfigureAwait(false);
                await Client.DeleteAsync(from, ct).ConfigureAwait(false);
            }

            return FileOperationResult<bool>.Success(true);
        }
        catch (Exception ex)
        {
            return FileOperationResult<bool>.Failure(ex);
        }
    }

    /// <summary>
    /// From the same account: a server-side copy. From anywhere else: the source streams into one PUT, or a multipart
    /// upload when large or of unknown length. Either way S3 commits the object whole or not at all, so a failed or
    /// cancelled copy leaves no partial file and keeps what was there before.
    /// </summary>
    public async Task<FileOperationResult<IFileItem>> CopyFileInAsync(IFileItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default)
    {
        try
        {
            var destination = ParseDestination(destinationPath);

            if (sourceItem is S3FileItem own && own.Connection.IsSameAccount(_connection))
            {
                if (own.Connection.IsSameBucket(_connection) && own.Key == destination)
                    return FileOperationResult<IFileItem>.Success(sourceItem);

                await Client.CopyObjectAsync(own.Connection, own.Key, own.Size, destination, progress, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                var (stream, length) = await sourceItem.OpenReadAsync(ct).ConfigureAwait(false);

                await using (stream)
                    await Client.UploadAsync(destination, stream, length, progress, ct).ConfigureAwait(false);
            }

            progress.Report(1);
            return FileOperationResult<IFileItem>.Success(await FindFileAsync(destination, ct).ConfigureAwait(false));
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

            if (sourceItem is S3FileItem own && own.Connection.IsSameAccount(_connection))
            {
                if (!(own.Connection.IsSameBucket(_connection) && own.Key == destination))
                {
                    await Client.CopyObjectAsync(own.Connection, own.Key, own.Size, destination, progress, ct)
                        .ConfigureAwait(false);
                    await own.Connection.Client.DeleteAsync(own.Key, ct).ConfigureAwait(false);
                }

                progress.Report(1);
                return FileOperationResult<IFileItem>.Success(await FindFileAsync(destination, ct).ConfigureAwait(false));
            }

            var copy = await CopyFileInAsync(sourceItem, destinationPath, progress, ct).ConfigureAwait(false);

            if (!copy.IsSuccess)
                return copy;

            await sourceItem.DeleteAsync(ct).ConfigureAwait(false);
            return copy;
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFileItem>.Failure(ex);
        }
    }

    /// <summary>Within the account every key under the folder is copied on the server; elsewhere — recurse.</summary>
    public async Task<FileOperationResult<IFolderItem?>> CopyFolderInAsync(IFolderItem sourceItem,
        string destinationPath, IProgress<double> progress, CancellationToken ct = default)
    {
        try
        {
            if (sourceItem is not S3FolderItem own || !own.Connection.IsSameAccount(_connection))
                return FileOperationResult<IFolderItem?>.Success(null);

            var destination = ParseDestination(destinationPath);

            if (own.Connection.IsSameBucket(_connection))
            {
                if (own.Path == destination)
                    throw new IOException($"Cannot copy '{own.Path}' onto itself.");

                S3Path.EnsureNotInside(own.Path, destination);
            }

            await CopyPrefixAsync(own.Connection, own.Path, destination, progress, ct).ConfigureAwait(false);
            progress.Report(1);

            return FileOperationResult<IFolderItem?>.Success(new S3FolderItem(_connection, destination, DateTimeOffset.Now));
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFolderItem?>.Failure(ex);
        }
    }

    public async Task<FileOperationResult<IFolderItem?>> TryMoveFolderAsync(IFolderItem sourceItem,
        string destinationPath, IProgress<double> progress, CancellationToken ct = default)
    {
        try
        {
            if (sourceItem is not S3FolderItem own || !own.Connection.IsSameAccount(_connection))
                return FileOperationResult<IFolderItem?>.Success(null);

            var destination = ParseDestination(destinationPath);
            var sameBucket = own.Connection.IsSameBucket(_connection);

            if (sameBucket)
                S3Path.EnsureNotInside(own.Path, destination);

            if (!sameBucket || own.Path != destination)
                await MovePrefixAsync(own.Connection, own.Path, destination, progress, ct).ConfigureAwait(false);

            progress.Report(1);
            return FileOperationResult<IFolderItem?>.Success(new S3FolderItem(_connection, destination, DateTimeOffset.Now));
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFolderItem?>.Failure(ex);
        }
    }

    public async Task<bool> ContainsFileAsync(string fileName, CancellationToken ct = default) =>
        await Client.HeadAsync(S3Path.Combine(_path, fileName), ct).ConfigureAwait(false) is not null;

    public Task<bool> ContainsFolderAsync(string folderName, CancellationToken ct = default) =>
        Client.FolderExistsAsync(S3Path.Prefix(S3Path.Combine(_path, folderName)), ct);

    public async Task<IFileItem> ResolveDestinationFileAsync(IFileItem sourceItem, bool overwrite,
        CancellationToken ct = default)
    {
        var name = overwrite ? sourceItem.Name : await ResolveFileNameAsync(sourceItem.Name, ct).ConfigureAwait(false);
        return new S3PlaceholderFileItem(_connection.FullName(S3Path.Combine(_path, name)), name);
    }

    public async Task<IFolderItem> ResolveDestinationFolderAsync(IFolderItem sourceItem, bool overwrite,
        CancellationToken ct = default)
    {
        var name = overwrite
            ? sourceItem.Name
            : await ResolveNameAsync(sourceItem.Name, sourceItem.Name, n => $"{sourceItem.Name} ({n})", ct)
                .ConfigureAwait(false);

        return new S3PlaceholderFolderItem(_connection, S3Path.Combine(_path, name));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static bool Matches(string name, string query) => name.Contains(query, StringComparison.OrdinalIgnoreCase);

    private Task<string> ResolveFileNameAsync(string name, CancellationToken ct)
    {
        var baseName = System.IO.Path.GetFileNameWithoutExtension(name);
        var extension = System.IO.Path.GetExtension(name);

        return ResolveNameAsync(name, baseName, n => $"{baseName} ({n}){extension}", ct);
    }

    /// <summary>
    /// <paramref name="name"/>, or the first free <paramref name="candidate"/> from 2 on. A name is taken by a file or
    /// a folder alike; every candidate starts with <paramref name="stem"/>, so one listing by it sees them all.
    /// </summary>
    private async Task<string> ResolveNameAsync(string name, string stem, Func<int, string> candidate,
        CancellationToken ct)
    {
        var prefix = S3Path.Prefix(_path);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        string? token = null;

        do
        {
            var page = await Client.ListAsync(prefix + stem, "/", token, null, ct).ConfigureAwait(false);

            foreach (var folder in page.CommonPrefixes)
                taken.Add(folder[prefix.Length..^1]);

            foreach (var entry in page.Objects)
                taken.Add(entry.Key[prefix.Length..].TrimEnd('/'));

            token = page.NextContinuationToken;
        } while (token is not null);

        if (!taken.Contains(name))
            return name;

        for (var index = 2; ; index++)
        {
            var next = candidate(index);

            if (!taken.Contains(next))
                return next;
        }
    }

    private async Task<bool> ExistsAsync(string path, CancellationToken ct) =>
        await Client.HeadAsync(path, ct).ConfigureAwait(false) is not null
        || await Client.FolderExistsAsync(S3Path.Prefix(path), ct).ConfigureAwait(false);

    private async Task<long> SizeOfAsync(string key, CancellationToken ct) =>
        (await Client.HeadAsync(key, ct).ConfigureAwait(false))?.Size
        ?? throw new FileNotFoundException($"'{key}' does not exist.", key);

    private async Task<IFileItem> FindFileAsync(string key, CancellationToken ct) =>
        await Client.HeadAsync(key, ct).ConfigureAwait(false) is { } info
            ? new S3FileItem(_connection, key, info.Size, info.LastModified, info.ETag)
            : new S3PlaceholderFileItem(_connection.FullName(key), S3Path.GetName(key));

    /// <summary>
    /// Copies every key under <paramref name="from"/> in <paramref name="source"/>'s bucket to the same place under
    /// <paramref name="to"/> here; a folder with no keys at all gets a marker, so it exists after the copy.
    /// Returns the source keys.
    /// </summary>
    private async Task<IReadOnlyList<string>> CopyPrefixAsync(S3Connection source, string from, string to,
        IProgress<double>? progress, CancellationToken ct)
    {
        var fromPrefix = S3Path.Prefix(from);
        var toPrefix = S3Path.Prefix(to);
        var entries = new List<S3ObjectInfo>();

        await foreach (var entry in source.Client.ListAllAsync(fromPrefix, ct).ConfigureAwait(false))
            entries.Add(entry);

        if (entries.Count == 0)
        {
            await Client.PutAsync(toPrefix, ReadOnlyMemory<byte>.Empty, null, ct).ConfigureAwait(false);
            return [];
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var key = toPrefix + entry.Key[fromPrefix.Length..];

            await Client.CopyObjectAsync(source, entry.Key, entry.Size, key, null, ct).ConfigureAwait(false);
            progress?.Report((double)(i + 1) / entries.Count);
        }

        return entries.Select(e => e.Key).ToList();
    }

    /// <summary>Copy, then delete the source keys — only once every copy has landed.</summary>
    private async Task MovePrefixAsync(S3Connection source, string from, string to, IProgress<double>? progress,
        CancellationToken ct)
    {
        var keys = await CopyPrefixAsync(source, from, to, progress, ct).ConfigureAwait(false);

        if (keys.Count > 0)
            await source.Client.DeleteManyAsync(keys, ct).ConfigureAwait(false);
    }

    /// <summary>The key or folder path of an item of this bucket; anything else cannot be renamed or deleted here.</summary>
    private (string Path, bool IsFolder) OwnTarget(IFileProviderItem item) => item switch
    {
        S3FileItem file when file.Connection.IsSameBucket(_connection) => (file.Key, false),
        S3FolderItem { Path.Length: > 0 } folder when folder.Connection.IsSameBucket(_connection) => (folder.Path, true),
        _ => throw new ArgumentException($"'{item.FullName}' is not an item of {_connection.RootFullName}.", nameof(item))
    };

    /// <summary>A destination must be a FullName in this bucket (<c>s3://host/bucket/key</c>); anything else is refused.</summary>
    private string ParseDestination(string destinationPath)
    {
        var root = _connection.RootFullName;

        if (destinationPath.StartsWith(root, StringComparison.Ordinal))
        {
            var key = S3Path.Normalize(destinationPath[root.Length..]);

            if (key.Length > 0)
                return key;
        }

        throw new ArgumentException($"'{destinationPath}' is not a path in {root}.", nameof(destinationPath));
    }
}
