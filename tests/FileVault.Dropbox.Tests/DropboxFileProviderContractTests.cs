using FileVault.Contract.Tests;
using FileVault.Core;

namespace FileVault.Dropbox.Tests;

/// <summary>
/// The shared provider contract against an in-memory Dropbox; needs no network or credentials. The provider works in
/// a subfolder so that paths below the root are exercised too.
/// </summary>
[TestFixture]
public class DropboxFileProviderContractTests : FileProviderContractTests
{
    private const string Root = "/Contract";

    private FakeDropbox _dropbox = null!;
    private DropboxClient _client = null!;

    protected override Task<IFileProvider> CreateProviderAsync()
    {
        _dropbox = new FakeDropbox();
        _dropbox.AddFolder(Root);
        _client = _dropbox.CreateClient();
        return Task.FromResult<IFileProvider>(_client.CreateProvider(Root));
    }

    protected override Task SeedFileAsync(string name, byte[] content)
    {
        _dropbox.AddFile($"{Root}/{name}", content);
        return Task.CompletedTask;
    }

    protected override Task SeedFolderAsync(string name)
    {
        _dropbox.AddFolder($"{Root}/{name}");
        return Task.CompletedTask;
    }

    protected override Task<byte[]> ReadFileAsync(string name) =>
        Task.FromResult(_dropbox.Read($"{Root}/{name}") ?? throw new FileNotFoundException(name));

    protected override Task<bool> FileExistsAsync(string name) => Task.FromResult(_dropbox.Read($"{Root}/{name}") is not null);

    protected override string GetDestPath(string name) => $"dropbox:{Root}/{name}";

    public override Task TearDown()
    {
        _dropbox.Dispose();
        return Task.CompletedTask;
    }
}
