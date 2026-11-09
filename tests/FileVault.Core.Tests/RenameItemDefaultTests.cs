using System.Runtime.CompilerServices;
using FileVault.Core;

namespace FileVault.Core.Tests;

/// <summary>The default <see cref="IFileProvider.RenameItemAsync"/> for providers that only implement RenameAsync.</summary>
[TestFixture]
public class RenameItemDefaultTests
{
    [Test]
    public async Task RenamesThroughRenameAsyncAndFindsTheNewNameInTheListing()
    {
        var provider = new RenameOnlyProvider(new Item("old.txt"), new Item("other.txt"));
        IFileProvider asInterface = provider;

        var result = await asInterface.RenameItemAsync(provider.Items[0], "new.txt");

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(result.Result, Is.SameAs(provider.Items[0]));
        Assert.That(result.Result!.Name, Is.EqualTo("new.txt"));
    }

    [Test]
    public async Task AFolderIsNotTakenForAFileOfTheSameName()
    {
        var folder = new FolderItem("new");
        var provider = new RenameOnlyProvider(folder, new Item("old"));
        IFileProvider asInterface = provider;

        var result = await asInterface.RenameItemAsync(provider.Items[1], "new");

        Assert.That(result.IsSuccess, Is.True, result.ErrorMessage);
        Assert.That(result.Result, Is.SameAs(provider.Items[1]));
    }

    [Test]
    public async Task AFailedRenameIsReturnedAsItIs()
    {
        var error = new IOException("taken");
        var provider = new RenameOnlyProvider(new Item("old.txt")) { RenameError = error };
        IFileProvider asInterface = provider;

        var result = await asInterface.RenameItemAsync(provider.Items[0], "new.txt");

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Exception, Is.SameAs(error));
    }

    [Test]
    public async Task ANameMissingFromTheListingIsAFailure()
    {
        var provider = new RenameOnlyProvider(new Item("old.txt")) { RenameDoesNothing = true };
        IFileProvider asInterface = provider;

        var result = await asInterface.RenameItemAsync(provider.Items[0], "new.txt");

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Exception, Is.InstanceOf<FileNotFoundException>());
    }

    [Test]
    public void MapKeepsAFailure()
    {
        var error = new IOException();

        Assert.That(FileOperationResult<int>.Failure(error).Map(_ => true).Exception, Is.SameAs(error));
        Assert.That(FileOperationResult<int>.Success(2).Map(v => v * 2).Result, Is.EqualTo(4));
    }

    private class Item(string name) : IFileProviderItem
    {
        public string Name { get; set; } = name;
        public string FullName => "/" + Name;
        public bool IsHidden => false;
        public bool IsSystem => false;
        public DateTimeOffset ChangedDate => DateTimeOffset.UnixEpoch;
        public long? Size => 0;
    }

    private sealed class FolderItem(string name) : Item(name), IFolderItem
    {
        public IFileProvider CreateProvider() => throw new NotSupportedException();
    }

    private sealed class RenameOnlyProvider(params Item[] items) : IFileProvider
    {
        public Item[] Items { get; } = items;
        public Exception? RenameError { get; init; }
        public bool RenameDoesNothing { get; init; }

        public string Name => "root";
        public string FullName => "/";

        public async IAsyncEnumerable<IFileProviderItem> GetItemsAsync(FileProviderFilter filter,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            foreach (var item in Items)
                yield return item;
        }

        public Task<FileOperationResult<bool>> RenameAsync(IFileProviderItem item, string newName,
            CancellationToken ct = default)
        {
            if (RenameError is not null)
                return Task.FromResult(FileOperationResult<bool>.Failure(RenameError));

            if (!RenameDoesNothing)
                ((Item)item).Name = newName;

            return Task.FromResult(FileOperationResult<bool>.Success(true));
        }

        public Task<FileOperationResult<IFolderItem>> CreateFolderAsync(string name, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<FileOperationResult<IFileItem>> CreateEmptyTextFileAsync(string name, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<FileOperationResult<IReadOnlyList<IFileProviderItem>>> DeleteAsync(
            IReadOnlyList<IFileProviderItem> items, bool toRecycleBin, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<FileOperationResult<IFileItem>> CopyFileInAsync(IFileItem sourceItem, string destinationPath,
            IProgress<double> progress, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<FileOperationResult<IFileItem>> MoveFileInAsync(IFileItem sourceItem, string destinationPath,
            IProgress<double> progress, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<FileOperationResult<IFolderItem?>> CopyFolderInAsync(IFolderItem sourceItem, string destinationPath,
            IProgress<double> progress, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<FileOperationResult<IFolderItem?>> TryMoveFolderAsync(IFolderItem sourceItem,
            string destinationPath, IProgress<double> progress, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> ContainsFileAsync(string fileName, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> ContainsFolderAsync(string folderName, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IFileItem> ResolveDestinationFileAsync(IFileItem sourceItem, bool overwrite,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IFolderItem> ResolveDestinationFolderAsync(IFolderItem sourceItem, bool overwrite,
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}
