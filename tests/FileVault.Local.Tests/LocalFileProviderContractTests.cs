using FileVault.Contract.Tests;
using FileVault.Core;
using FileVault.Local;

namespace FileVault.Local.Tests;

[TestFixture]
public class LocalFileProviderContractTests : FileProviderContractTests
{
    private string _tempDir = null!;

    protected override Task<IFileProvider> CreateProviderAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var resolver = new LocalFileProviderResolver();
        return resolver.ResolveAsync(_tempDir)!;
    }

    protected override Task SeedFileAsync(string name, byte[] content)
    {
        File.WriteAllBytes(Path.Combine(_tempDir, name), content);
        return Task.CompletedTask;
    }

    protected override Task SeedFolderAsync(string name)
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, name));
        return Task.CompletedTask;
    }

    protected override Task<byte[]> ReadFileAsync(string name)
        => Task.FromResult(File.ReadAllBytes(Path.Combine(_tempDir, name)));

    protected override Task<bool> FileExistsAsync(string name)
        => Task.FromResult(File.Exists(Path.Combine(_tempDir, name)));

    protected override string GetDestPath(string name) => Path.Combine(_tempDir, name);

    public override Task TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
        return Task.CompletedTask;
    }
}
