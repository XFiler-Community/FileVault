using FileVault.Core;
using FileVault.Ftp;
using FileVault.Local;

if (args.Length < 1)
{
    PrintUsage();
    return 1;
}

var resolver = new CompositeFileProviderResolver([
    new LocalFileProviderResolver(),
    // FTP resolver can be added here: new FtpFileProviderResolver(new FtpConnection { Host = "..." })
]);

var command = args[0].ToLowerInvariant();

return command switch
{
    "list" => args.Length < 2 ? Usage() : await ListAsync(resolver, args[1]),
    "mkdir" => args.Length < 3 ? Usage() : await MkdirAsync(resolver, args[1], args[2]),
    "copy" => args.Length < 3 ? Usage() : await CopyAsync(resolver, args[1], args[2]),
    "move" => args.Length < 3 ? Usage() : await MoveAsync(resolver, args[1], args[2]),
    "drives" => await DrivesAsync(resolver),
    _ => Usage(),
};

static async Task<int> ListAsync(IFileProviderResolver resolver, string path)
{
    var provider = await resolver.ResolveAsync(path);
    if (provider is null)
    {
        Console.Error.WriteLine($"Unknown path: {path}");
        return 1;
    }

    await foreach (var item in provider.GetItemsAsync(FileProviderFilter.ShowAll))
    {
        var size = item.Size.HasValue ? $"{item.Size,15:N0} bytes" : $"{"<dir>",20}";
        var hidden = item.IsHidden ? "[H]" : "   ";
        Console.WriteLine($"{hidden} {item.Name,-50} {size}");
    }
    return 0;
}

static async Task<int> MkdirAsync(IFileProviderResolver resolver, string path, string name)
{
    var provider = await resolver.ResolveAsync(path);
    if (provider is null)
    {
        Console.Error.WriteLine($"Unknown path: {path}");
        return 1;
    }

    var result = await provider.CreateFolderAsync(name);
    if (result.IsSuccess)
    {
        Console.WriteLine($"Created: {result.Result!.FullName}");
        return 0;
    }
    Console.Error.WriteLine($"Error: {result.ErrorMessage}");
    return 1;
}

static async Task<int> CopyAsync(IFileProviderResolver resolver, string sourcePath, string destPath)
{
    var destDir = Path.GetDirectoryName(destPath) ?? destPath;
    var destProvider = await resolver.ResolveAsync(destDir);
    if (destProvider is null)
    {
        Console.Error.WriteLine($"Unknown destination: {destDir}");
        return 1;
    }

    var sourceProvider = await resolver.ResolveAsync(Path.GetDirectoryName(sourcePath) ?? sourcePath);
    if (sourceProvider is null)
    {
        Console.Error.WriteLine($"Unknown source: {sourcePath}");
        return 1;
    }

    var fileName = Path.GetFileName(sourcePath);
    var items = await sourceProvider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();
    var sourceItem = items.OfType<IFileItem>().FirstOrDefault(i => i.Name == fileName);
    if (sourceItem is null)
    {
        Console.Error.WriteLine($"File not found: {sourcePath}");
        return 1;
    }

    var progress = new Progress<double>(v => Console.Write($"\rCopying... {v:P0}   "));
    var result = await destProvider.CopyFileInAsync(sourceItem, destPath, progress);
    Console.WriteLine();

    if (result.IsSuccess)
    {
        Console.WriteLine($"Copied to: {result.Result!.FullName}");
        return 0;
    }
    Console.Error.WriteLine($"Error: {result.ErrorMessage}");
    return 1;
}

static async Task<int> MoveAsync(IFileProviderResolver resolver, string sourcePath, string destPath)
{
    var destDir = Path.GetDirectoryName(destPath) ?? destPath;
    var destProvider = await resolver.ResolveAsync(destDir);
    if (destProvider is null)
    {
        Console.Error.WriteLine($"Unknown destination: {destDir}");
        return 1;
    }

    var sourceProvider = await resolver.ResolveAsync(Path.GetDirectoryName(sourcePath) ?? sourcePath);
    if (sourceProvider is null)
    {
        Console.Error.WriteLine($"Unknown source: {sourcePath}");
        return 1;
    }

    var fileName = Path.GetFileName(sourcePath);
    var items = await sourceProvider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();
    var sourceItem = items.OfType<IFileItem>().FirstOrDefault(i => i.Name == fileName);
    if (sourceItem is null)
    {
        Console.Error.WriteLine($"File not found: {sourcePath}");
        return 1;
    }

    var progress = new Progress<double>(v => Console.Write($"\rMoving... {v:P0}   "));
    var result = await destProvider.MoveFileInAsync(sourceItem, destPath, progress);
    Console.WriteLine();

    if (result.IsSuccess)
    {
        Console.WriteLine($"Moved to: {result.Result!.FullName}");
        return 0;
    }
    Console.Error.WriteLine($"Error: {result.ErrorMessage}");
    return 1;
}

static async Task<int> DrivesAsync(IFileProviderResolver resolver)
{
    var drives = await resolver.GetDrivesAsync();
    foreach (var drive in drives)
    {
        var free = drive.TotalFreeSpace > 0 ? $"{drive.TotalFreeSpace / 1_073_741_824.0:F1} GB free" : "";
        Console.WriteLine($"{drive.Name,-20} {free}");
    }
    return 0;
}

static int Usage()
{
    PrintUsage();
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  filevault list <path>");
    Console.WriteLine("  filevault mkdir <path> <name>");
    Console.WriteLine("  filevault copy <source> <dest>");
    Console.WriteLine("  filevault move <source> <dest>");
    Console.WriteLine("  filevault drives");
}
