using FileVault.Core;

namespace FileVault.Local;

public sealed class LocalFileProvider(string rootPath) : IFileProvider
{
    public string Name => Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : rootPath;
    public string FullName => rootPath;

    public async IAsyncEnumerable<IFileProviderItem> GetItemsAsync(FileProviderFilter filter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var attrs = FileAttributes.None;
        if (!filter.ShowSystemItems) attrs |= FileAttributes.System;
        if (!filter.ShowHiddenItems) attrs |= FileAttributes.Hidden;
        var options = new EnumerationOptions { AttributesToSkip = attrs };

        foreach (var dir in Directory.EnumerateDirectories(rootPath, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            yield return new SystemFolderItem(new DirectoryInfo(dir));
        }

        foreach (var file in Directory.EnumerateFiles(rootPath, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            yield return new SystemFileItem(new FileInfo(file));
        }

        await Task.CompletedTask;
    }

    public Task<FileOperationResult<IFolderItem>> CreateFolderAsync(string name, CancellationToken ct = default)
    {
        try
        {
            var resolvedName = ResolveFolderName(name);
            var path = Path.Combine(rootPath, resolvedName);
            Directory.CreateDirectory(path);
            IFolderItem item = new SystemFolderItem(new DirectoryInfo(path));
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
            var path = Path.Combine(rootPath, name);
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
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                if (item is IFileItem)
                    File.Delete(item.FullName);
                else
                    Directory.Delete(item.FullName, recursive: true);
            }
            return Task.FromResult(FileOperationResult<IReadOnlyList<IFileProviderItem>>.Success(items));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FileOperationResult<IReadOnlyList<IFileProviderItem>>.Failure(ex));
        }
    }

    public Task<FileOperationResult<bool>> RenameAsync(IFileProviderItem item, string newName, CancellationToken ct = default)
    {
        try
        {
            var dir = Path.GetDirectoryName(item.FullName)!;
            var newPath = Path.Combine(dir, newName);

            if (item is IFileItem)
                File.Move(item.FullName, newPath);
            else
                Directory.Move(item.FullName, newPath);

            return Task.FromResult(FileOperationResult<bool>.Success(true));
        }
        catch (Exception ex)
        {
            return Task.FromResult(FileOperationResult<bool>.Failure(ex));
        }
    }

    public async Task<FileOperationResult<IFileItem>> CopyFileInAsync(IFileItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default)
    {
        try
        {
            (var src, long len) = await sourceItem.OpenReadAsync(ct).ConfigureAwait(false);
            await using (src)
            await using (var dst = File.Create(destinationPath))
            {
                await FileOperationsHelper.CopyAsync(src, len, dst, progress, ct).ConfigureAwait(false);
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
        try
        {
            await CopyDirectoryAsync(sourceItem.FullName, destinationPath, ct).ConfigureAwait(false);
            progress.Report(1.0);
            IFolderItem? result = new SystemFolderItem(new DirectoryInfo(destinationPath));
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
            if (!OnSameDrive(sourceItem.FullName, destinationPath))
                return Task.FromResult(FileOperationResult<IFolderItem?>.Success(null));

            Directory.Move(sourceItem.FullName, destinationPath);
            progress.Report(1.0);
            IFolderItem? result = new SystemFolderItem(new DirectoryInfo(destinationPath));
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
        if (overwrite)
            return new SystemFileItem(Path.Combine(rootPath, sourceItem.Name));

        var name = await FindUniqueFileNameAsync(sourceItem.Name, ct).ConfigureAwait(false);
        return new SystemFileItem(Path.Combine(rootPath, name));
    }

    public async Task<IFolderItem> ResolveDestinationFolderAsync(IFolderItem sourceItem, bool overwrite, CancellationToken ct = default)
    {
        if (overwrite)
            return new SystemFolderItem(new DirectoryInfo(Path.Combine(rootPath, sourceItem.Name)));

        var name = await FindUniqueFolderNameAsync(sourceItem.Name, ct).ConfigureAwait(false);
        return new SystemFolderItem(new DirectoryInfo(Path.Combine(rootPath, name)));
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

    private static async Task CopyDirectoryAsync(string source, string dest, CancellationToken ct)
    {
        Directory.CreateDirectory(dest);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            ct.ThrowIfCancellationRequested();
            await using var src = File.OpenRead(file);
            await using var dst = File.Create(Path.Combine(dest, Path.GetFileName(file)));
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            ct.ThrowIfCancellationRequested();
            await CopyDirectoryAsync(dir, Path.Combine(dest, Path.GetFileName(dir)), ct).ConfigureAwait(false);
        }
    }
}
