namespace FileVault.Core;

public interface IFileProviderItem
{
    string Name { get; }
    string FullName { get; }
    bool IsHidden { get; }
    bool IsSystem { get; }
    DateTimeOffset ChangedDate { get; }
    long? Size { get; }
}
