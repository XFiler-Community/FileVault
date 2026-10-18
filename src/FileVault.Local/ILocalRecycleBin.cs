namespace FileVault.Local;

/// <summary>
/// Platform recycle bin (Trash on macOS, Recycle Bin on Windows) used by
/// <see cref="LocalFileProvider.DeleteAsync"/> when <c>toRecycleBin</c> is <c>true</c>.
/// Without it, recycling fails with <see cref="NotSupportedException"/> instead of deleting permanently.
/// </summary>
public interface ILocalRecycleBin
{
    /// <summary>Moves the item to the recycle bin; throws if it could not be moved.</summary>
    void MoveToRecycleBin(string path, bool isDirectory);
}
