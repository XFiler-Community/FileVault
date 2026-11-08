namespace FileVault.Core;

/// <summary>
/// Optional: a provider that can add up the sizes of the folders in its folder — every file at any depth below each
/// one — the cheapest way its backend allows (a size the backend keeps, a flat or recursive listing), so a client does
/// not have to list every subfolder itself. Check with <c>provider is IFolderSizeProvider sizes</c>.
/// </summary>
public interface IFolderSizeProvider : IFileProvider
{
    /// <summary>
    /// Snapshots of the sizes of this folder's subfolders as they are added up: each one holds every folder seen so
    /// far with the bytes counted so far, and the last one is <see cref="FolderSizes.IsComplete"/>. In the complete
    /// one a subfolder that is missing holds no files. Throws when the backend fails; cancellation stops the walk.
    /// </summary>
    IAsyncEnumerable<FolderSizes> MeasureFoldersAsync(CancellationToken ct = default);
}

/// <summary>Bytes below each subfolder of a provider's folder, by the subfolder's <see cref="IFileProviderItem.Name"/>.</summary>
public sealed class FolderSizes(IReadOnlyDictionary<string, long> bytes, bool isComplete)
{
    public IReadOnlyDictionary<string, long> Bytes { get; } = bytes;

    /// <summary>Every file was counted: the sizes are final.</summary>
    public bool IsComplete { get; } = isComplete;

    /// <summary>The size of the subfolder named <paramref name="name"/>; null when it was not seen yet.</summary>
    public long? Find(string name) => Bytes.TryGetValue(name, out var bytes) ? bytes : null;
}
