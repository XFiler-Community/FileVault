using FileVault.Contract.Tests;
using FileVault.Core;

namespace FileVault.S3.Tests;

/// <summary>
/// The shared provider contract against the in-memory S3, below a nested prefix so prefix handling is exercised;
/// needs no network. Folders are seeded as markers, the way S3 consoles create them.
/// </summary>
[TestFixture]
public class S3FakeContractTests : FileProviderContractTests
{
    private const string Bucket = "contract";
    private const string Root = "base/dir";

    private FakeS3 _s3 = null!;
    private S3Connection _connection = null!;

    protected override Task<IFileProvider> CreateProviderAsync()
    {
        _s3 = new FakeS3 { PageSize = 2 };
        _connection = _s3.Connect(Bucket);
        _s3.Put(Bucket, "base/dir/", []);
        _s3.Put(Bucket, "base/sibling.txt", [9]);
        return Task.FromResult<IFileProvider>(new S3FileProvider(_connection, Root));
    }

    protected override Task SeedFileAsync(string name, byte[] content)
    {
        _s3.Put(Bucket, $"{Root}/{name}", content);
        return Task.CompletedTask;
    }

    protected override Task SeedFolderAsync(string name)
    {
        _s3.Put(Bucket, $"{Root}/{name}/", []);
        return Task.CompletedTask;
    }

    protected override Task<byte[]> ReadFileAsync(string name) =>
        Task.FromResult(_s3.Get(Bucket, $"{Root}/{name}") ?? throw new FileNotFoundException(name));

    protected override Task<bool> FileExistsAsync(string name) =>
        Task.FromResult(_s3.Get(Bucket, $"{Root}/{name}") is not null);

    protected override string GetDestPath(string name) => _connection.FullName($"{Root}/{name}");

    public override Task TearDown()
    {
        _s3.Dispose();
        return Task.CompletedTask;
    }
}
