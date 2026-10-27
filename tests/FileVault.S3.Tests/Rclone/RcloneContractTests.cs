using FileVault.Contract.Tests;
using FileVault.Core;

namespace FileVault.S3.Tests.Rclone;

/// <summary>
/// The shared provider contract against a real S3 server (<c>rclone serve s3</c>), each test in its own folder.
/// Seeds and checks go through the served folder on disk, so they see what the server really stored.
/// </summary>
[TestFixture]
[Category("Integration")]
public class RcloneContractTests : FileProviderContractTests
{
    private S3Connection _connection = null!;
    private string _prefix = null!;

    private string Folder => Path.Combine(RcloneS3Server.BucketFolder, _prefix);

    protected override Task<IFileProvider> CreateProviderAsync()
    {
        _connection = RcloneS3Server.Connect();
        _prefix = $"contract-{Guid.NewGuid():N}";
        return Task.FromResult<IFileProvider>(new S3FileProvider(_connection, _prefix));
    }

    [SetUp]
    public void SkipWhatRcloneCannotStore()
    {
        // rclone keeps keys as files, so the zero-byte "name/" marker of an empty folder becomes a file "name"
        // (its documented limitation: no empty folders below the bucket). The in-memory S3 covers markers.
        if (TestContext.CurrentContext.Test.MethodName == nameof(CreateFolderAsync_ReturnsItemAndCreatesDirectory))
            Assert.Ignore("rclone serve s3 cannot keep folder markers; covered by S3FakeContractTests.");
    }

    protected override async Task SeedFileAsync(string name, byte[] content)
    {
        Directory.CreateDirectory(Folder);
        await File.WriteAllBytesAsync(Path.Combine(Folder, name), content);
    }

    protected override Task SeedFolderAsync(string name)
    {
        Directory.CreateDirectory(Path.Combine(Folder, name));
        return Task.CompletedTask;
    }

    protected override Task<byte[]> ReadFileAsync(string name) => File.ReadAllBytesAsync(Path.Combine(Folder, name));

    protected override Task<bool> FileExistsAsync(string name) => Task.FromResult(File.Exists(Path.Combine(Folder, name)));

    protected override string GetDestPath(string name) => _connection.FullName($"{_prefix}/{name}");

    public override Task TearDown()
    {
        if (Directory.Exists(Folder))
            Directory.Delete(Folder, recursive: true);

        return Task.CompletedTask;
    }
}
