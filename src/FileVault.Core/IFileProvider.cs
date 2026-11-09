namespace FileVault.Core;

public interface IFileProvider
{
    string Name { get; }
    string FullName { get; }

    IAsyncEnumerable<IFileProviderItem> GetItemsAsync(FileProviderFilter filter, CancellationToken ct = default);

    Task<FileOperationResult<IFolderItem>> CreateFolderAsync(string name, CancellationToken ct = default);
    Task<FileOperationResult<IFileItem>> CreateEmptyTextFileAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// <paramref name="toRecycleBin"/> = true moves items to the backend's trash. A provider without a trash
    /// returns Failure(<see cref="NotSupportedException"/>) and leaves the items untouched — it never
    /// falls back to permanent deletion, so the caller can ask the user first.
    /// </summary>
    Task<FileOperationResult<IReadOnlyList<IFileProviderItem>>> DeleteAsync(
        IReadOnlyList<IFileProviderItem> items, bool toRecycleBin, CancellationToken ct = default);

    /// <summary>
    /// Renames <paramref name="item"/> in place. Kept for existing callers: <see cref="RenameItemAsync"/> does the
    /// same and returns the item under its new name.
    /// </summary>
    Task<FileOperationResult<bool>> RenameAsync(IFileProviderItem item, string newName, CancellationToken ct = default);

    /// <summary>
    /// Renames <paramref name="item"/> in place and returns it under its new name — the old item still points at the
    /// old name. Every FileVault provider builds the result from the backend's answer or the item's own metadata.
    /// This default, for providers written before it, renames through <see cref="RenameAsync"/> and finds the new
    /// name in this provider's listing, which assumes the item lives in this provider's folder.
    /// </summary>
    async Task<FileOperationResult<IFileProviderItem>> RenameItemAsync(IFileProviderItem item, string newName,
        CancellationToken ct = default)
    {
        var renamed = await RenameAsync(item, newName, ct).ConfigureAwait(false);

        if (!renamed.IsSuccess)
            return FileOperationResult<IFileProviderItem>.Failure(renamed.Exception!);

        try
        {
            var isFolder = item is IFolderItem;

            await foreach (var candidate in GetItemsAsync(FileProviderFilter.ShowAll, ct).ConfigureAwait(false))
            {
                if (candidate.Name == newName && candidate is IFolderItem == isFolder)
                    return FileOperationResult<IFileProviderItem>.Success(candidate);
            }

            throw new FileNotFoundException($"'{newName}' was renamed but is not in the listing of '{FullName}'.",
                newName);
        }
        catch (Exception ex)
        {
            return FileOperationResult<IFileProviderItem>.Failure(ex);
        }
    }

    Task<FileOperationResult<IFileItem>> CopyFileInAsync(IFileItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default);

    Task<FileOperationResult<IFileItem>> MoveFileInAsync(IFileItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default);

    /// <summary>
    /// Same-provider: нативная копия. Cross-provider: возвращает Result == null — сигнал для оркестратора делать рекурсию.
    /// </summary>
    Task<FileOperationResult<IFolderItem?>> CopyFolderInAsync(IFolderItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default);

    /// <summary>
    /// Same-provider: нативное перемещение. Cross-provider: возвращает Result == null.
    /// </summary>
    Task<FileOperationResult<IFolderItem?>> TryMoveFolderAsync(IFolderItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default);

    Task<bool> ContainsFileAsync(string fileName, CancellationToken ct = default);
    Task<bool> ContainsFolderAsync(string folderName, CancellationToken ct = default);

    Task<IFileItem> ResolveDestinationFileAsync(IFileItem sourceItem, bool overwrite, CancellationToken ct = default);
    Task<IFolderItem> ResolveDestinationFolderAsync(IFolderItem sourceItem, bool overwrite, CancellationToken ct = default);
}
