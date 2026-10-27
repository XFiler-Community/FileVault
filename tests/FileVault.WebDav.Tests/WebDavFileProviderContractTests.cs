using FileVault.Contract.Tests;
using FileVault.Core;

namespace FileVault.WebDav.Tests;

/// <summary>
/// The contract against <c>rclone serve webdav</c>. Each test gets a folder whose name has a space, Cyrillic
/// letters, <c>#</c> and <c>%</c>, so every request exercises the URL encoding.
/// </summary>
[TestFixture]
[Category("Integration")]
public class WebDavFileProviderContractTests : FileProviderContractTests
{
    private RcloneWebDavServer _server = null!;
    private WebDavConnection _connection = null!;
    private string _folder = null!;

    private string LocalFolder => Path.Combine(_server.RootDirectory, _folder);

    [OneTimeSetUp]
    public async Task StartServer()
    {
        _server = await RcloneWebDavServer.StartOrIgnoreAsync();
        _connection = _server.CreateConnection();
    }

    [OneTimeTearDown]
    public async Task StopServer()
    {
        _connection?.Dispose();

        if (_server is not null)
            await _server.DisposeAsync();
    }

    protected override Task<IFileProvider> CreateProviderAsync()
    {
        _folder = $"Тест #{Guid.NewGuid():N} 100%";
        Directory.CreateDirectory(LocalFolder);
        return Task.FromResult<IFileProvider>(new WebDavFileProvider(_connection, _folder));
    }

    protected override Task SeedFileAsync(string name, byte[] content) =>
        File.WriteAllBytesAsync(Path.Combine(LocalFolder, name), content);

    protected override Task SeedFolderAsync(string name)
    {
        Directory.CreateDirectory(Path.Combine(LocalFolder, name));
        return Task.CompletedTask;
    }

    protected override Task<byte[]> ReadFileAsync(string name) => File.ReadAllBytesAsync(Path.Combine(LocalFolder, name));

    protected override Task<bool> FileExistsAsync(string name) => Task.FromResult(File.Exists(Path.Combine(LocalFolder, name)));

    protected override string GetDestPath(string name) => _connection.GetFullName($"{_folder}/{name}");

    public override Task TearDown()
    {
        if (_folder is not null && Directory.Exists(LocalFolder))
            Directory.Delete(LocalFolder, recursive: true);

        return Task.CompletedTask;
    }
}
