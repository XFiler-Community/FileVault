using FileVault.Core;

namespace FileVault.GitHub;

/// <summary>
/// A file of a branch. <see cref="ChangedDate"/> is the date of the commit it was listed at: the tree does not say
/// when each file last changed, the file's history does (<see cref="GitHubRepository.GetCommitsAsync"/>).
/// </summary>
public sealed class GitHubFileItem(GitHubRepository repository, GitHubEntry entry, DateTimeOffset date) : IFileItem
{
    public GitHubRepository Repository => repository;
    public GitHubEntry Entry => entry;

    /// <summary>The blob the file had when listed: pass it to <see cref="GitHubRepository.WriteFileAsync"/>.</summary>
    public string BlobSha => entry.Sha;

    public bool IsLfs => entry.IsLfs;
    public string Path => entry.Path;

    public string Name => entry.Name;
    public string FullName => repository.FullName(entry.Path);
    public bool IsHidden => entry.Name.StartsWith('.');
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => date;
    public long Size => entry.Size;
    long? IFileProviderItem.Size => entry.Size;
    public string Extension => System.IO.Path.GetExtension(entry.Name);
    public string NameWithoutExtension => System.IO.Path.GetFileNameWithoutExtension(entry.Name);

    public async Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default)
    {
        var stream = await repository.OpenReadAsync(entry, ct).ConfigureAwait(false);
        return (stream, stream.Length);
    }

    public async Task DeleteAsync(CancellationToken ct = default) =>
        await repository.CommitAsync([GitHubChange.Delete(entry.Path)], $"Delete {entry.Path}", null, ct)
            .ConfigureAwait(false);
}
