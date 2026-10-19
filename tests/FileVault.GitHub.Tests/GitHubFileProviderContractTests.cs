using FileVault.Contract.Tests;
using FileVault.Core;

namespace FileVault.GitHub.Tests;

/// <summary>The shared provider contract against an in-memory GitHub; needs no network or credentials.</summary>
[TestFixture]
public class GitHubFileProviderContractTests : FileProviderContractTests
{
    private FakeGitHub _github = null!;
    private GitHubRepository _repository = null!;

    protected override Task<IFileProvider> CreateProviderAsync()
    {
        _github = new FakeGitHub();
        _repository = _github.CreateRepository();
        return Task.FromResult<IFileProvider>(_repository.CreateProvider());
    }

    protected override Task SeedFileAsync(string name, byte[] content)
    {
        _github.Commit(new Dictionary<string, byte[]?> { [name] = content });
        return Task.CompletedTask;
    }

    // Git keeps no empty folders: a folder exists through a file in it
    protected override Task SeedFolderAsync(string name)
    {
        _github.Commit(new Dictionary<string, byte[]?> { [$"{name}/.gitkeep"] = [] });
        return Task.CompletedTask;
    }

    protected override Task<byte[]> ReadFileAsync(string name) =>
        Task.FromResult(_github.Read(name) ?? throw new FileNotFoundException(name));

    protected override Task<bool> FileExistsAsync(string name) => Task.FromResult(_github.Read(name) is not null);

    protected override string GetDestPath(string name) => _repository.FullName(name);

    public override Task TearDown()
    {
        _github.Dispose();
        return Task.CompletedTask;
    }
}
