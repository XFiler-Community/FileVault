using FileVault.Core;

namespace FileVault.GitHub;

internal sealed class GitHubPlaceholderFolderItem(GitHubRepository repository, string path) : IFolderItem
{
    public string Name => path[(path.LastIndexOf('/') + 1)..];
    public string FullName => repository.FullName(path);
    public bool IsHidden => false;
    public bool IsSystem => false;
    public DateTimeOffset ChangedDate => DateTimeOffset.Now;
    public long? Size => null;

    public IFileProvider CreateProvider() => repository.CreateProvider(GitHubPath.Parent(path));
}
