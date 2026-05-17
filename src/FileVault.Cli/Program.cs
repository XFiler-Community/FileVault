using FileVault.Core;
using FileVault.Local;

if (args.Length < 1)
{
    PrintUsage();
    return 1;
}

var command = args[0].ToLowerInvariant();

return command switch
{
    "list" => args.Length < 2 ? Usage() : await ListAsync(args[1]),
    "mkdir" => args.Length < 3 ? Usage() : await MkdirAsync(args[1], args[2]),
    "copy" => args.Length < 3 ? Usage() : await CopyAsync(args[1], args[2]),
    "move" => args.Length < 3 ? Usage() : await MoveAsync(args[1], args[2]),
    "drives" => Drives(),
    _ => Usage(),
};

static async Task<int> ListAsync(string path)
{
    var provider = CreateLocalProvider(path);
    if (provider is null)
    {
        Console.Error.WriteLine($"Directory not found: {path}");
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

static async Task<int> MkdirAsync(string path, string name)
{
    var provider = CreateLocalProvider(path);
    if (provider is null)
    {
        Console.Error.WriteLine($"Directory not found: {path}");
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

static async Task<int> CopyAsync(string sourcePath, string destPath)
{
    var fullSourcePath = Path.GetFullPath(sourcePath);
    var fullDestPath = Path.GetFullPath(destPath);
    var destDir = Path.GetDirectoryName(fullDestPath) ?? fullDestPath;
    var destProvider = CreateLocalProvider(destDir);
    if (destProvider is null)
    {
        Console.Error.WriteLine($"Destination directory not found: {destDir}");
        return 1;
    }

    var sourceDir = Path.GetDirectoryName(fullSourcePath) ?? fullSourcePath;
    var sourceProvider = CreateLocalProvider(sourceDir);
    if (sourceProvider is null)
    {
        Console.Error.WriteLine($"Source directory not found: {sourceDir}");
        return 1;
    }

    var fileName = Path.GetFileName(fullSourcePath);
    var items = await sourceProvider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();
    var sourceItem = items.OfType<IFileItem>().FirstOrDefault(i => i.Name == fileName);
    if (sourceItem is null)
    {
        Console.Error.WriteLine($"File not found: {sourcePath}");
        return 1;
    }

    var progress = new Progress<double>(v => Console.Write($"\rCopying... {v:P0}   "));
    var result = await destProvider.CopyFileInAsync(sourceItem, fullDestPath, progress);
    Console.WriteLine();

    if (result.IsSuccess)
    {
        Console.WriteLine($"Copied to: {result.Result!.FullName}");
        return 0;
    }
    Console.Error.WriteLine($"Error: {result.ErrorMessage}");
    return 1;
}

static async Task<int> MoveAsync(string sourcePath, string destPath)
{
    var fullSourcePath = Path.GetFullPath(sourcePath);
    var fullDestPath = Path.GetFullPath(destPath);
    var destDir = Path.GetDirectoryName(fullDestPath) ?? fullDestPath;
    var destProvider = CreateLocalProvider(destDir);
    if (destProvider is null)
    {
        Console.Error.WriteLine($"Destination directory not found: {destDir}");
        return 1;
    }

    var sourceDir = Path.GetDirectoryName(fullSourcePath) ?? fullSourcePath;
    var sourceProvider = CreateLocalProvider(sourceDir);
    if (sourceProvider is null)
    {
        Console.Error.WriteLine($"Source directory not found: {sourceDir}");
        return 1;
    }

    var fileName = Path.GetFileName(fullSourcePath);
    var items = await sourceProvider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();
    var sourceItem = items.OfType<IFileItem>().FirstOrDefault(i => i.Name == fileName);
    if (sourceItem is null)
    {
        Console.Error.WriteLine($"File not found: {sourcePath}");
        return 1;
    }

    var progress = new Progress<double>(v => Console.Write($"\rMoving... {v:P0}   "));
    var result = await destProvider.MoveFileInAsync(sourceItem, fullDestPath, progress);
    Console.WriteLine();

    if (result.IsSuccess)
    {
        Console.WriteLine($"Moved to: {result.Result!.FullName}");
        return 0;
    }
    Console.Error.WriteLine($"Error: {result.ErrorMessage}");
    return 1;
}

static int Drives()
{
    var drives = DriveInfo.GetDrives()
        .Where(drive => drive.IsReady)
        .Select(drive => new LocalDriveItem(drive));

    foreach (var drive in drives)
    {
        var free = drive.TotalFreeSpace > 0 ? $"{drive.TotalFreeSpace / 1_073_741_824.0:F1} GB free" : "";
        Console.WriteLine($"{drive.Name,-20} {free}");
    }
    return 0;
}

static LocalFileProvider? CreateLocalProvider(string path)
{
    var fullPath = Path.GetFullPath(path);
    return Directory.Exists(fullPath) ? new LocalFileProvider(fullPath) : null;
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
