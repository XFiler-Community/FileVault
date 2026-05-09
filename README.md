# FileVault

[![CI](https://github.com/XFiler-Community/FileVault/actions/workflows/ci.yml/badge.svg)](https://github.com/XFiler-Community/FileVault/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/FileVault.Core.svg)](https://www.nuget.org/packages/FileVault.Core)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A platform-independent .NET library for working with file systems. The single `IFileProvider` interface abstracts away the differences between the local file system, FTP, SFTP, and cloud storage. Extracted from the file subsystem of [X-Filer Desktop](https://github.com/XFiler-Community/X-Filer.Desktop).

> **Supported backends:** Local FS · FTP/FTPS · SFTP · Yandex Disk · Google Drive

## Packages

| Package | NuGet | Description |
|---|---|---|
| `FileVault.Core` | [![NuGet](https://img.shields.io/nuget/v/FileVault.Core.svg)](https://www.nuget.org/packages/FileVault.Core) | Contracts and helpers. Depends only on BCL |
| `FileVault.Local` | [![NuGet](https://img.shields.io/nuget/v/FileVault.Local.svg)](https://www.nuget.org/packages/FileVault.Local) | Local file system |
| `FileVault.Ftp` | [![NuGet](https://img.shields.io/nuget/v/FileVault.Ftp.svg)](https://www.nuget.org/packages/FileVault.Ftp) | FTP/FTPS via [FluentFTP](https://github.com/robinrodricks/FluentFTP) |
| `FileVault.Sftp` | [![NuGet](https://img.shields.io/nuget/v/FileVault.Sftp.svg)](https://www.nuget.org/packages/FileVault.Sftp) | SFTP via [SSH.NET](https://github.com/sshnet/SSH.NET) |
| `FileVault.YandexDisk` | [![NuGet](https://img.shields.io/nuget/v/FileVault.YandexDisk.svg)](https://www.nuget.org/packages/FileVault.YandexDisk) | Yandex Disk via REST API |
| `FileVault.GoogleDrive` | [![NuGet](https://img.shields.io/nuget/v/FileVault.GoogleDrive.svg)](https://www.nuget.org/packages/FileVault.GoogleDrive) | Google Drive via Drive API v3 |

## Installation

```shell
dotnet add package FileVault.Core
dotnet add package FileVault.Local
dotnet add package FileVault.Ftp
dotnet add package FileVault.Sftp
dotnet add package FileVault.YandexDisk
dotnet add package FileVault.GoogleDrive
```

## Quick Start

### Local file system

```csharp
using FileVault.Core;
using FileVault.Local;

var resolver = new LocalFileProviderResolver();
var provider = await resolver.ResolveAsync("/home/user/Documents");

await foreach (var item in provider!.GetItemsAsync(FileProviderFilter.Default))
{
    Console.WriteLine($"{item.Name}  {item.Size?.ToString() ?? "<dir>"}");
}
```

### FTP

```csharp
using FileVault.Ftp;

var resolver = new FtpFileProviderResolver(new FtpConnection
{
    Host     = "ftp.example.com",
    Username = "user",
    Password = "secret",
});

var provider = await resolver.ResolveAsync("ftp://ftp.example.com/public");
```

### SFTP

```csharp
using FileVault.Sftp;

var resolver = new SftpFileProviderResolver(new SftpConnection
{
    Host           = "ssh.example.com",
    Username       = "deploy",
    PrivateKeyPath = "~/.ssh/id_rsa",   // or use Password
});

var provider = await resolver.ResolveAsync("sftp://ssh.example.com/var/data");
```

### Yandex Disk

```csharp
using FileVault.YandexDisk;

var resolver = new YandexDiskFileProviderResolver(oauthToken: "y0_AgAAAA...");

// Disk root
var provider = await resolver.ResolveAsync("x-filevault:yandex-disk");

// Specific folder
var photos = await resolver.ResolveAsync("disk:/Photos");
```

### Google Drive

```csharp
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using FileVault.GoogleDrive;

// Build an authenticated DriveService (OAuth2 user credentials example)
var credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
    GoogleClientSecrets.FromFile("client_secrets.json").Secrets,
    [DriveService.Scope.Drive],
    user: "user",
    CancellationToken.None);

var driveService = new DriveService(new BaseClientService.Initializer
{
    HttpClientInitializer = credential,
    ApplicationName = "MyApp",
});

// FileVault takes the pre-authenticated service — no OAuth logic inside the provider
var resolver = new GoogleDriveFileProviderResolver(driveService);

// My Drive root
var root = await resolver.ResolveAsync("x-filevault:google-drive");

// Specific folder by Drive file ID
var folder = await resolver.ResolveAsync("gdrive:1BxiMVs0XRA5nFMdKvBdBZjgmUUqptlbs");
```

> **Authentication** is the caller's responsibility. Pass any `DriveService` — OAuth2 user, service account, or impersonation. See [Google Auth Library docs](https://developers.google.com/api-client-library/dotnet/guide/aaa_oauth).

### Multiple providers at once

`CompositeFileProviderResolver` queries a list of resolvers and returns the first match. Use it in integration entry points (UI, CLI).

```csharp
using FileVault.Core;
using FileVault.Ftp;
using FileVault.Local;

var resolver = new CompositeFileProviderResolver([
    new LocalFileProviderResolver(),
    new FtpFileProviderResolver(new FtpConnection { Host = "ftp.example.com", ... }),
]);

// Works for any path — local or FTP
var provider = await resolver.ResolveAsync(path);
```

## Core Operations

All operations return `FileOperationResult<T>` — exceptions never propagate to the caller.

```csharp
// Create folder (appends " (2)", " (3)", etc. on name conflict)
var result = await provider.CreateFolderAsync("NewFolder");
if (result.IsSuccess)
    Console.WriteLine(result.Result!.FullName);

// Copy a file INTO this provider from any other provider
var copyResult = await destProvider.CopyFileInAsync(
    sourceFileItem,
    "/backups/report.pdf",
    new Progress<double>(v => Console.Write($"\r{v:P0}"))
);

// Move a file (same-drive: native move; cross-provider: copy + delete)
await destProvider.MoveFileInAsync(sourceFileItem, destPath, progress);

// Delete
await provider.DeleteAsync([item], toRecycleBin: false);

// Rename
await provider.RenameAsync(item, "new-name.txt");

// Resolve a unique destination path (file is NOT created on disk)
var dest = await provider.ResolveDestinationFileAsync(sourceFile, overwrite: false);
// "report.pdf" → "report (2).pdf" if the name is already taken
```

### Copying folders across providers

`CopyFolderInAsync` returns `null` in `Result` — not an error — when the provider does not support native folder copying (FTP, SFTP, or cross-provider scenarios). The orchestrator is expected to recurse manually:

```csharp
async Task RecursiveCopyAsync(IFolderItem source, IFileProvider dest, string destPath)
{
    // Attempt native copy (Yandex Disk: server-side; others: returns null)
    var result = await dest.CopyFolderInAsync(source, destPath, progress);
    if (result.IsSuccess && result.Result is not null)
        return; // done

    // No native support — recurse manually
    await dest.CreateFolderAsync(source.Name);
    var subProvider = source.CreateProvider();

    await foreach (var item in subProvider.GetItemsAsync(FileProviderFilter.Default))
    {
        if (item is IFileItem file)
            await dest.CopyFileInAsync(file, $"{destPath}/{file.Name}", progress);
        else if (item is IFolderItem folder)
            await RecursiveCopyAsync(folder, dest, $"{destPath}/{folder.Name}");
    }
}
```

## Microsoft.Extensions.DependencyInjection

Each package registers its own `IFileProviderResolver`. `CompositeFileProviderResolver` aggregates them.

```csharp
services.AddSingleton<IFileProviderResolver, LocalFileProviderResolver>();

services.AddSingleton<IFileProviderResolver>(_ =>
    new FtpFileProviderResolver(new FtpConnection
    {
        Host     = config["Ftp:Host"]!,
        Username = config["Ftp:Username"],
        Password = config["Ftp:Password"],
    }));

services.AddSingleton<IFileProviderResolver>(_ =>
    new GoogleDriveFileProviderResolver(/* your DriveService */));

services.AddSingleton<IFileProviderResolver>(sp =>
    new CompositeFileProviderResolver(sp.GetServices<IFileProviderResolver>()));
```

## Adding a Custom Provider

1. Implement `IFileProvider` (and `IFileItem`, `IFolderItem` for your items).
2. Implement `IFileProviderResolver`.
3. Write tests by inheriting `FileProviderContractTests` from the `FileVault.Contract.Tests` package — all 17 contract tests run automatically.

```csharp
// In your MyProvider.Tests project:
// <PackageReference Include="FileVault.Contract.Tests" Version="..." />

public class MyProviderContractTests : FileProviderContractTests
{
    protected override Task<IFileProvider> CreateProviderAsync() { ... }
    protected override Task SeedFileAsync(string name, byte[] content) { ... }
    protected override Task SeedFolderAsync(string name) { ... }
    protected override Task<byte[]> ReadFileAsync(string name) { ... }
    protected override Task<bool> FileExistsAsync(string name) { ... }
    protected override string GetDestPath(string name) { ... }
    public override Task TearDown() { ... }
}
```

## Development

### Prerequisites

- .NET 10 SDK
- Docker (for FTP integration tests via TestContainers)

### Build & Test

```shell
dotnet build
dotnet test tests/FileVault.Core.Tests
dotnet test tests/FileVault.Local.Tests

# FTP integration tests (requires Docker)
dotnet test tests/FileVault.Ftp.Tests --filter "Category=Integration"

# Google Drive integration tests (requires a service account JSON)
export GOOGLE_DRIVE_SERVICE_ACCOUNT_JSON=/path/to/sa.json
dotnet test tests/FileVault.GoogleDrive.Tests --filter "Category=Integration"
```

### Releasing

Versions are driven by git tags:

```shell
git tag v1.2.0
git push origin v1.2.0
```

GitHub Actions will pack and publish all packages to NuGet.org automatically.

## License

[MIT](LICENSE)
