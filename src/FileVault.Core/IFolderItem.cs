namespace FileVault.Core;

public interface IFolderItem : IFileProviderItem
{
    IFileProvider CreateProvider();
}
