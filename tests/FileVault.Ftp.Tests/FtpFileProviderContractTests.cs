using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FileVault.Contract.Tests;
using FileVault.Core;
using FileVault.Ftp;
using FluentFTP;

namespace FileVault.Ftp.Tests;

[TestFixture]
[Category("Integration")]
public class FtpFileProviderContractTests : FileProviderContractTests
{
    private IContainer _container = null!;
    private AsyncFtpClient _client = null!;
    private string _testRoot = null!;

    private const string FtpUser = "testuser";
    private const string FtpPass = "testpass";

    [OneTimeSetUp]
    public async Task StartContainer()
    {
        _container = new ContainerBuilder("delfer/alpine-ftp-server")
            .WithPortBinding(21, true)
            .WithPortBinding(21100, true)
            .WithPortBinding(21101, true)
            .WithPortBinding(21102, true)
            .WithPortBinding(21103, true)
            .WithPortBinding(21104, true)
            .WithPortBinding(21105, true)
            .WithEnvironment("USERS", $"{FtpUser}|{FtpPass}")
            .WithEnvironment("MIN_PORT", "21100")
            .WithEnvironment("MAX_PORT", "21105")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilContainerIsHealthy())
            .Build();

        await _container.StartAsync();

        var ftpPort = _container.GetMappedPublicPort(21);
        _client = new AsyncFtpClient("localhost", FtpUser, FtpPass, ftpPort);
        _client.Config.DataConnectionType = FtpDataConnectionType.PASV;
        _client.Config.PassiveBlockedPorts = [];

        await _client.Connect();
    }

    [OneTimeTearDown]
    public async Task StopContainer()
    {
        await _client.Disconnect();
        _client.Dispose();
        await _container.StopAsync();
        await _container.DisposeAsync();
    }

    protected override async Task<IFileProvider> CreateProviderAsync()
    {
        _testRoot = $"/test_{Guid.NewGuid():N}";
        await _client.CreateDirectory(_testRoot);
        return new FtpFileProvider(_client, _testRoot);
    }

    protected override async Task SeedFileAsync(string name, byte[] content)
    {
        using var stream = new MemoryStream(content);
        await _client.UploadStream(stream, $"{_testRoot}/{name}", FtpRemoteExists.Overwrite);
    }

    protected override async Task SeedFolderAsync(string name)
        => await _client.CreateDirectory($"{_testRoot}/{name}");

    protected override async Task<byte[]> ReadFileAsync(string name)
    {
        using var stream = new MemoryStream();
        await _client.DownloadStream(stream, $"{_testRoot}/{name}");
        return stream.ToArray();
    }

    protected override async Task<bool> FileExistsAsync(string name)
        => await _client.FileExists($"{_testRoot}/{name}");

    protected override string GetDestPath(string name) => $"{_testRoot}/{name}";

    public override async Task TearDown()
    {
        if (await _client.DirectoryExists(_testRoot))
            await _client.DeleteDirectory(_testRoot);
    }
}
