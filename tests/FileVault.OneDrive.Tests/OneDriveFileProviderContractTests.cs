using FileVault.Contract.Tests;
using FileVault.Core;

namespace FileVault.OneDrive.Tests;

/// <summary>The shared provider contract against an in-memory Microsoft Graph; needs no network or credentials.</summary>
[TestFixture]
public class OneDriveFileProviderContractTests : FileProviderContractTests
{
    private FakeGraph _graph = null!;
    private string _folderId = null!;

    protected override Task<IFileProvider> CreateProviderAsync()
    {
        _graph = new FakeGraph();
        _folderId = _graph.AddFolder(FakeGraph.RootId, "contract");
        return Task.FromResult<IFileProvider>(_graph.CreateClient().CreateProvider(_folderId, "contract"));
    }

    protected override Task SeedFileAsync(string name, byte[] content)
    {
        _graph.AddFile(_folderId, name, content);
        return Task.CompletedTask;
    }

    protected override Task SeedFolderAsync(string name)
    {
        _graph.AddFolder(_folderId, name);
        return Task.CompletedTask;
    }

    protected override Task<byte[]> ReadFileAsync(string name) =>
        Task.FromResult(_graph.Find(_folderId, name)?.Content ?? throw new FileNotFoundException(name));

    protected override Task<bool> FileExistsAsync(string name) =>
        Task.FromResult(_graph.Find(_folderId, name) is { IsFolder: false });

    protected override string GetDestPath(string name) => $"onedrive:{_folderId}/{name}";

    public override Task TearDown()
    {
        _graph.Dispose();
        return Task.CompletedTask;
    }
}
