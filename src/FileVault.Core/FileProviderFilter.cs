namespace FileVault.Core;

public sealed class FileProviderFilter
{
    public bool ShowHiddenItems { get; init; }
    public bool ShowSystemItems { get; init; }

    public static FileProviderFilter Default { get; } = new();
    public static FileProviderFilter ShowAll { get; } = new() { ShowHiddenItems = true, ShowSystemItems = true };
}
