namespace FileVault.Core;

public interface IFileProvider
{
    string Name { get; }
    string FullName { get; }

    IAsyncEnumerable<IFileProviderItem> GetItemsAsync(FileProviderFilter filter, CancellationToken ct = default);

    Task<FileOperationResult<IFolderItem>> CreateFolderAsync(string name, CancellationToken ct = default);
    Task<FileOperationResult<IFileItem>> CreateEmptyTextFileAsync(string name, CancellationToken ct = default);

    Task<FileOperationResult<IReadOnlyList<IFileProviderItem>>> DeleteAsync(
        IReadOnlyList<IFileProviderItem> items, bool toRecycleBin, CancellationToken ct = default);

    Task<FileOperationResult<bool>> RenameAsync(IFileProviderItem item, string newName, CancellationToken ct = default);

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
