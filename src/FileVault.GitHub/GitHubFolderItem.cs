using FileVault.Core;

namespace FileVault.GitHub;

public sealed class GitHubFolderItem(GitHubRepository repository, string path, DateTimeOffset date) : IFolderItem
{
    public GitHubRepository Repository => repository;
    public string Path => path;

    public string Name => path.Length == 0 ? repository.Name : path[(path.LastIndexOf('/') + 1)..];
    public string FullName => repository.FullName(path);
    public bool IsHidden => Name.StartsWith('.');
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => date;
    public long? Size => null;

    public IFileProvider CreateProvider() => repository.CreateProvider(path);
}
