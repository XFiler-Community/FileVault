using FileVault.Core;

namespace FileVault.Local;

public sealed class LocalFileProvider(string rootPath, ILocalRecycleBin? recycleBin = null) : IFileProvider, IParallelTransferProvider
{
    /// <inheritdoc />
    // Disk I/O; the other side of a copy is usually a network backend.
    public int MaxParallelTransfers => 4;

    public string Name => Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : rootPath;
    public string FullName => rootPath;

    public async IAsyncEnumerable<IFileProviderItem> GetItemsAsync(FileProviderFilter filter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var attrs = FileAttributes.None;
        if (!filter.ShowSystemItems) attrs |= FileAttributes.System;
        if (!filter.ShowHiddenItems) attrs |= FileAttributes.Hidden;
        var options = new EnumerationOptions { AttributesToSkip = attrs };

        // Reading a directory blocks — for seconds on an SMB share or \\wsl.localhost — so it runs on the thread
        // pool a batch at a time, never on the caller's thread. Opening the directory is the first blocking call.
        IEnumerator<FileSystemInfo>? entries = null;
        var files = new List<IFileProviderItem>();

        try
        {
            entries = await Task.Run(
                () => new DirectoryInfo(rootPath).EnumerateFileSystemInfos("*", options).GetEnumerator(), ct)
                .ConfigureAwait(false);

            var more = true;
            while (more)
            {
                var batch = await Task.Run(() => ReadBatch(entries, ct), ct).ConfigureAwait(false);
                more = batch.Count == ListingBatchSize;

                foreach (var item in batch)
                {
                    if (item is IFolderItem)
                        yield return item;
                    else
                        files.Add(item);
                }
            }
        }
        finally
        {
            entries?.Dispose();
        }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            yield return file;
        }
    }

    private const int ListingBatchSize = 256;

    // Items are made from the directory entry: attributes, times and size come with it, so no item costs a stat of
    // its own. Folders come first in a listing, files after them.
    private List<IFileProviderItem> ReadBatch(IEnumerator<FileSystemInfo> entries, CancellationToken ct)
    {
        var batch = new List<IFileProviderItem>(ListingBatchSize);

        while (batch.Count < ListingBatchSize && entries.MoveNext())
        {
            ct.ThrowIfCancellationRequested();
            batch.Add(entries.Current switch
            {
                DirectoryInfo dir => new SystemFolderItem(dir, recycleBin),
                var info => new SystemFileItem((FileInfo)info)
            });
        }

        return batch;
    }

    public Task<FileOperationResult<IFolderItem>> CreateFolderAsync(string name, CancellationToken ct = default)
    {
        try
        {
            var resolvedName = ResolveFolderName(LocalFileNames.Validate(name));
            var path = Path.Combine(rootPath, resolvedName);
            Directory.CreateDirectory(path);
            IFolderItem item = new SystemFolderItem(new DirectoryInfo(path), recycleBin);
            return Task.FromResult(FileOperationResult<IFolderItem>.Success(item));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FileOperationResult<IFolderItem>.Failure(ex));
        }
    }

    public Task<FileOperationResult<IFileItem>> CreateEmptyTextFileAsync(string name, CancellationToken ct = default)
    {
        try
        {
            var path = Path.Combine(rootPath, LocalFileNames.Validate(name));
            File.WriteAllText(path, string.Empty);
            IFileItem item = new SystemFileItem(new FileInfo(path));
            return Task.FromResult(FileOperationResult<IFileItem>.Success(item));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FileOperationResult<IFileItem>.Failure(ex));
        }
    }

    public Task<FileOperationResult<IReadOnlyList<IFileProviderItem>>> DeleteAsync(
        IReadOnlyList<IFileProviderItem> items, bool toRecycleBin, CancellationToken ct = default)
    {
        try
        {
            if (toRecycleBin && recycleBin is null)
                throw new NotSupportedException("Recycle bin is not available for this location.");

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                var isDirectory = item is not IFileItem;
                if (toRecycleBin)
                    recycleBin!.MoveToRecycleBin(item.FullName, isDirectory);
                else if (isDirectory)
                    Directory.Delete(item.FullName, recursive: true);
                else
                    File.Delete(item.FullName);
            }
            return Task.FromResult(FileOperationResult<IReadOnlyList<IFileProviderItem>>.Success(items));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FileOperationResult<IReadOnlyList<IFileProviderItem>>.Failure(ex));
        }
    }

    public async Task<FileOperationResult<bool>> RenameAsync(IFileProviderItem item, string newName, CancellationToken ct = default) =>
        (await RenameItemAsync(item, newName, ct).ConfigureAwait(false)).Map(_ => true);

    /// <summary>
    /// Renames within the item's own folder — not necessarily this provider's (search results list items of
    /// subfolders) — and reads the renamed item back from disk.
    /// </summary>
    public Task<FileOperationResult<IFileProviderItem>> RenameItemAsync(IFileProviderItem item, string newName,
        CancellationToken ct = default)
    {
        try
        {
            var dir = Path.GetDirectoryName(item.FullName)!;
            var newPath = Path.Combine(dir, LocalFileNames.Validate(newName));

            IFileProviderItem renamed;
            if (item is IFileItem)
            {
                File.Move(item.FullName, newPath);
                renamed = new SystemFileItem(new FileInfo(newPath));
            }
            else
            {
                Directory.Move(item.FullName, newPath);
                renamed = new SystemFolderItem(new DirectoryInfo(newPath), recycleBin);
            }

            return Task.FromResult(FileOperationResult<IFileProviderItem>.Success(renamed));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FileOperationResult<IFileProviderItem>.Failure(ex));
        }
    }

    public async Task<FileOperationResult<IFileItem>> CopyFileInAsync(IFileItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default)
    {
        try
        {
            (var src, long len) = await sourceItem.OpenReadAsync(ct).ConfigureAwait(false);
            await using (src)
            {
                await CopyToFileAsync(src, len, destinationPath, progress, ct).ConfigureAwait(false);
            }
            IFileItem result = new SystemFileItem(new FileInfo(destinationPath));
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
            if (sourceItem is SystemFileItem && OnSameDrive(sourceItem.FullName, destinationPath))
            {
                File.Move(sourceItem.FullName, destinationPath, overwrite: true);
                progress.Report(1.0);
            }
            else
            {
                var copyResult = await CopyFileInAsync(sourceItem, destinationPath, progress, ct).ConfigureAwait(false);
                if (!copyResult.IsSuccess)
                    return copyResult;
                await sourceItem.DeleteAsync(ct).ConfigureAwait(false);
            }
            IFileItem result = new SystemFileItem(new FileInfo(destinationPath));
            return FileOperationResult<IFileItem>.Success(result);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFileItem>.Failure(ex);
        }
    }

    public async Task<FileOperationResult<IFolderItem?>> CopyFolderInAsync(IFolderItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default)
    {
        // Cross-provider source (cloud, FTP, SFTP): no local path to copy from — let the orchestrator recurse.
        if (sourceItem is not SystemFolderItem)
            return FileOperationResult<IFolderItem?>.Success(null);

        try
        {
            await CopyDirectoryAsync(sourceItem.FullName, destinationPath, ct).ConfigureAwait(false);
            progress.Report(1.0);
            IFolderItem? result = new SystemFolderItem(new DirectoryInfo(destinationPath), recycleBin);
            return FileOperationResult<IFolderItem?>.Success(result);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFolderItem?>.Failure(ex);
        }
    }

    public Task<FileOperationResult<IFolderItem?>> TryMoveFolderAsync(IFolderItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default)
    {
        try
        {
            if (sourceItem is not SystemFolderItem || !OnSameDrive(sourceItem.FullName, destinationPath))
                return Task.FromResult(FileOperationResult<IFolderItem?>.Success(null));

            Directory.Move(sourceItem.FullName, destinationPath);
            progress.Report(1.0);
            IFolderItem? result = new SystemFolderItem(new DirectoryInfo(destinationPath), recycleBin);
            return Task.FromResult(FileOperationResult<IFolderItem?>.Success(result));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FileOperationResult<IFolderItem?>.Failure(ex));
        }
    }

    public Task<bool> ContainsFileAsync(string fileName, CancellationToken ct = default)
        => Task.FromResult(File.Exists(Path.Combine(rootPath, fileName)));

    public Task<bool> ContainsFolderAsync(string folderName, CancellationToken ct = default)
        => Task.FromResult(Directory.Exists(Path.Combine(rootPath, folderName)));

    public async Task<IFileItem> ResolveDestinationFileAsync(IFileItem sourceItem, bool overwrite, CancellationToken ct = default)
    {
        var sourceName = LocalFileNames.Sanitize(sourceItem.Name);
        if (overwrite)
            return new SystemFileItem(Path.Combine(rootPath, sourceName));

        var name = await FindUniqueFileNameAsync(sourceName, ct).ConfigureAwait(false);
        return new SystemFileItem(Path.Combine(rootPath, name));
    }

    public async Task<IFolderItem> ResolveDestinationFolderAsync(IFolderItem sourceItem, bool overwrite, CancellationToken ct = default)
    {
        var sourceName = LocalFileNames.Sanitize(sourceItem.Name);
        if (overwrite)
            return new SystemFolderItem(new DirectoryInfo(Path.Combine(rootPath, sourceName)), recycleBin);

        var name = await FindUniqueFolderNameAsync(sourceName, ct).ConfigureAwait(false);
        return new SystemFolderItem(new DirectoryInfo(Path.Combine(rootPath, name)), recycleBin);
    }

    private async Task<string> FindUniqueFileNameAsync(string fileName, CancellationToken ct)
    {
        if (!await ContainsFileAsync(fileName, ct).ConfigureAwait(false))
            return fileName;

        var ext = Path.GetExtension(fileName);
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var index = 2;
        string candidate;
        do
        {
            candidate = $"{baseName} ({index}){ext}";
            index++;
        }
        while (await ContainsFileAsync(candidate, ct).ConfigureAwait(false));
        return candidate;
    }

    private async Task<string> FindUniqueFolderNameAsync(string folderName, CancellationToken ct)
    {
        if (!await ContainsFolderAsync(folderName, ct).ConfigureAwait(false))
            return folderName;

        var index = 2;
        string candidate;
        do
        {
            candidate = $"{folderName} ({index})";
            index++;
        }
        while (await ContainsFolderAsync(candidate, ct).ConfigureAwait(false));
        return candidate;
    }

    private string ResolveFolderName(string name)
    {
        if (!Directory.Exists(Path.Combine(rootPath, name)))
            return name;

        var index = 2;
        string candidate;
        do
        {
            candidate = $"{name} ({index})";
            index++;
        }
        while (Directory.Exists(Path.Combine(rootPath, candidate)));
        return candidate;
    }

    private static bool OnSameDrive(string path1, string path2) =>
        string.Equals(
            Path.GetPathRoot(path1),
            Path.GetPathRoot(path2),
            StringComparison.OrdinalIgnoreCase);

    // The copy goes to a temp file next to the destination and replaces it only when complete:
    // a cancelled or failed copy leaves neither a truncated file nor a damaged original (Replace).
    private static async Task CopyToFileAsync(Stream src, long totalBytes, string destinationPath,
        IProgress<double>? progress, CancellationToken ct)
    {
        var tempPath = GetPartialPath(destinationPath);
        try
        {
            await using (var dst = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             bufferSize: 4096, FileOptions.Asynchronous))
            {
                await FileOperationsHelper.CopyAsync(src, totalBytes, dst, progress, ct).ConfigureAwait(false);
            }
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    private static string GetPartialPath(string destinationPath)
    {
        var name = Path.GetFileName(destinationPath);
        // Keep the temp name within the 255-char file name limit.
        var prefix = name.Length <= 200 ? name : string.Empty;
        return Path.Combine(Path.GetDirectoryName(destinationPath) ?? string.Empty,
            $"{prefix}.filevault-partial-{Guid.NewGuid():N}");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // The original error is what the caller needs; a leftover temp file is the lesser problem.
        }
    }

    private static async Task CopyDirectoryAsync(string source, string dest, CancellationToken ct)
    {
        Directory.CreateDirectory(dest);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            ct.ThrowIfCancellationRequested();
            await using var src = File.OpenRead(file);
            await CopyToFileAsync(src, src.Length, Path.Combine(dest, Path.GetFileName(file)), null, ct).ConfigureAwait(false);
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            ct.ThrowIfCancellationRequested();
            await CopyDirectoryAsync(dir, Path.Combine(dest, Path.GetFileName(dir)), ct).ConfigureAwait(false);
        }
    }
}
