# FileVault

[![CI](https://github.com/XFiler-Community/FileVault/actions/workflows/ci.yml/badge.svg)](https://github.com/XFiler-Community/FileVault/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/FileVault.Core.svg)](https://www.nuget.org/packages/FileVault.Core)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A platform-independent .NET library for working with file systems. The single `IFileProvider` interface abstracts away the differences between the local file system, FTP, SFTP, and cloud storage. Extracted from the file subsystem of [X-Filer Desktop](https://x-filer.com).

FileVault does not parse user-entered paths or choose a backend for an application. The client resolves its own routes, bookmarks, accounts, and credentials, then creates the appropriate `IFileProvider` directly.

> **Supported backends:** Local FS · FTP/FTPS · SFTP · Yandex Disk · Google Drive · GitHub repositories (with Git LFS) · WSL via UNC paths (Windows)

## Packages

| Package | NuGet | Description |
|---|---|---|
| `FileVault.Core` | [![NuGet](https://img.shields.io/nuget/v/FileVault.Core.svg)](https://www.nuget.org/packages/FileVault.Core) | Contracts and helpers. Depends only on BCL |
| `FileVault.Local` | [![NuGet](https://img.shields.io/nuget/v/FileVault.Local.svg)](https://www.nuget.org/packages/FileVault.Local) | Local file system |
| `FileVault.Ftp` | [![NuGet](https://img.shields.io/nuget/v/FileVault.Ftp.svg)](https://www.nuget.org/packages/FileVault.Ftp) | FTP/FTPS via [FluentFTP](https://github.com/robinrodricks/FluentFTP) |
| `FileVault.Sftp` | [![NuGet](https://img.shields.io/nuget/v/FileVault.Sftp.svg)](https://www.nuget.org/packages/FileVault.Sftp) | SFTP via [SSH.NET](https://github.com/sshnet/SSH.NET) |
| `FileVault.YandexDisk` | [![NuGet](https://img.shields.io/nuget/v/FileVault.YandexDisk.svg)](https://www.nuget.org/packages/FileVault.YandexDisk) | Yandex Disk via REST API |
| `FileVault.GoogleDrive` | [![NuGet](https://img.shields.io/nuget/v/FileVault.GoogleDrive.svg)](https://www.nuget.org/packages/FileVault.GoogleDrive) | Google Drive via Drive API v3 |
| `FileVault.GitHub` | [![NuGet](https://img.shields.io/nuget/v/FileVault.GitHub.svg)](https://www.nuget.org/packages/FileVault.GitHub) | One branch of a GitHub repository via REST API: commits as writes, Git LFS reads, history |
| `FileVault.Wsl` | [![NuGet](https://img.shields.io/nuget/v/FileVault.Wsl.svg)](https://www.nuget.org/packages/FileVault.Wsl) | WSL distros via `\\wsl$\` UNC paths (Windows 10 1903+) |

## Installation

```shell
dotnet add package FileVault.Core
dotnet add package FileVault.Local
dotnet add package FileVault.Ftp
dotnet add package FileVault.Sftp
dotnet add package FileVault.YandexDisk
dotnet add package FileVault.GoogleDrive
dotnet add package FileVault.GitHub
dotnet add package FileVault.Wsl        # Windows only
```

## Quick Start

### Local file system

```csharp
using FileVault.Core;
using FileVault.Local;

var provider = new LocalFileProvider("/home/user/Documents");

await foreach (var item in provider.GetItemsAsync(FileProviderFilter.Default))
{
    Console.WriteLine($"{item.Name}  {item.Size?.ToString() ?? "<dir>"}");
}
```

Recycle bin support is opt-in: pass an `ILocalRecycleBin` (e.g. backed by the OS Trash / Recycle Bin API).
It flows to every `SystemFolderItem.CreateProvider()` below that root. Without it,
`DeleteAsync(..., toRecycleBin: true)` fails with `NotSupportedException` and deletes nothing.

```csharp
var provider = new LocalFileProvider("/home/user/Documents", recycleBin);
```

### FTP

```csharp
using FileVault.Ftp;
using FluentFTP;

var client = new AsyncFtpClient("ftp.example.com", "user", "secret");
await client.Connect();

var provider = new FtpFileProvider(client, "/public");
```

### SFTP

```csharp
using FileVault.Sftp;
using Renci.SshNet;

var privateKeyFile = new PrivateKeyFile("/home/user/.ssh/id_rsa");
var client = new SftpClient("ssh.example.com", "deploy", privateKeyFile);
client.Connect();

var provider = new SftpFileProvider(client, "/var/data");
```

### Yandex Disk

```csharp
using Egorozh.YandexDisk.Client.Http;
using FileVault.YandexDisk;

var api = new DiskHttpApi("y0_AgAAAA...", logSaver: null);

// Disk root
var provider = new YandexDiskFileProvider(api, "disk:/");

// Specific folder
var photos = new YandexDiskFileProvider(api, "disk:/Photos");
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

// My Drive root
var root = new GoogleDriveFileProvider(driveService, "root");

// Specific folder by Drive file ID
var folder = new GoogleDriveFileProvider(driveService, "1BxiMVs0XRA5nFMdKvBdBZjgmUUqptlbs");

// A file by its path below a folder (the most recently modified of same-named items)
if (await root.FindAsync("Projects/art-life/notes.md") is IFileRevisionsItem notes)
{
    // Past states Drive keeps, oldest first (binary files: 30 days / 100 revisions; none for Google Docs)
    var revisions = await notes.GetRevisionsAsync();
    var (stream, length) = await notes.OpenRevisionAsync(revisions[^2]);
}
```

> **Authentication** is the caller's responsibility. Pass any `DriveService` — OAuth2 user, service account, or impersonation. See [Google Auth Library docs](https://developers.google.com/api-client-library/dotnet/guide/aaa_oauth).

### GitHub

```csharp
using FileVault.GitHub;

// One branch of a repository; the token is any OAuth / GitHub App user token (null: public repos, read-only)
var repository = new GitHubRepository(new GitHubRepositoryOptions
{
    Owner = "anna",
    Name = "art-life",
    Branch = "main",
    Token = token
}, httpClient);

var root = repository.CreateProvider();              // github:anna/art-life@main:/
var folder = repository.CreateProvider("applications");

// A whole subtree in one request (paths from the repository root, each folder before its content);
// folders below it are cached, so listing them afterwards costs nothing
var everything = await repository.ListRecursiveAsync("", resolveLfsSizes: false);
var claudeFiles = everything.Where(e => e.Name == "CLAUDE.md");

// Every write is one commit on top of the branch head, never a force push.
// An edit that must not overwrite what changed meanwhile:
var result = await repository.WriteFileAsync("CLAUDE.md", bytes, expectedBlobSha: opened.BlobSha,
    message: "Edit CLAUDE.md");
// result.Status: Written · ChangedMeanwhile (nothing written) · StoredInLfs (refused)

// Several files as one commit, each only while it is still what the caller saw
var edit = await repository.CommitAsync([
    GitHubFileEdit.Write("notes.md", bytes, expectedSha: notes.Sha),
    GitHubFileEdit.Restore("cover.psd", earlierCover, expectedSha: cover.Sha),  // reuses the old blob, LFS too
    GitHubFileEdit.Delete("draft.md", expectedSha: draft.Sha)
], "Tidy up");
// edit.Status: Committed · NothingToCommit · ChangedMeanwhile (edit.Paths: the files in the way) · StoredInLfs

// Undo a whole commit with a new one; refused (ChangedMeanwhile) when a later commit touched its files again
var revert = await repository.RevertAsync(agentCommitSha);
var changes = await repository.DiffAsync(fromSnapshot, toSnapshot);    // files that differ, Before / After

// History for change feeds
var commits = await repository.GetCommitsAsync(path: "deadlines.csv", perPage: 30);
var commit = await repository.GetCommitAsync(commits[0].Sha);          // with the files it changed
var ahead = await repository.CompareAsync("main", "claude/open-calls");  // a cloud agent's branch
```

> Git keeps no empty folders, so `CreateFolderAsync` puts an empty `.gitkeep` in the new folder. A delete is a commit
> the history can undo, so `toRecycleBin: true` deletes too. Files tracked by Git LFS (root `.gitattributes`) are
> read through LFS and listed with their real size; writing them is refused. `ChangedDate` is the date of the
> commit the folder was listed at — per-file dates come from `GetCommitsAsync(path)`. `ListRecursiveAsync` falls back to
> reading folder by folder when GitHub truncates a recursive tree (over 100 000 entries or 7 MB).

### WSL (Windows Subsystem for Linux)

> Requires Windows 10 1903+ with WSL installed. No-op on macOS/Linux.

```csharp
using FileVault.Local;
using FileVault.Wsl;

var root = new WslDriveItem("Ubuntu").CreateProvider();
var provider = new LocalFileProvider(@"\\wsl$\Ubuntu\home\user\projects");
```

### Application-level routing

If an application supports multiple backends, it should store enough information to construct the right provider itself: local path, FTP remote path plus authenticated client, Google Drive folder ID plus `DriveService`, and so on.

```csharp
using FileVault.Core;
using FileVault.Ftp;
using FileVault.Local;
using FileVault.GoogleDrive;

IFileProvider provider = location.Kind switch
{
    LocationKind.Local => new LocalFileProvider(location.Path),
    LocationKind.Ftp => new FtpFileProvider(ftpClient, location.RemotePath),
    LocationKind.GoogleDrive => new GoogleDriveFileProvider(driveService, location.FolderId),
    _ => throw new NotSupportedException($"Unsupported location kind: {location.Kind}")
};
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

// Delete: toRecycleBin: true moves to the backend's trash (Local with ILocalRecycleBin, Yandex Disk,
// Google Drive; GitHub deletes with a commit the history can undo); other providers return
// Failure(NotSupportedException) and delete nothing.
var deleted = await provider.DeleteAsync([item], toRecycleBin: true);
if (deleted.Exception is NotSupportedException)
    deleted = await provider.DeleteAsync([item], toRecycleBin: false); // permanent — confirm with the user first

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

Register authenticated clients, services, or app-specific factories. FileVault itself does not provide a DI routing abstraction.

```csharp
services.AddSingleton(driveService);
services.AddSingleton(ftpClient);

services.AddTransient<Func<SavedLocation, IFileProvider>>(sp => location => location.Kind switch
{
    LocationKind.Local => new LocalFileProvider(location.Path),
    LocationKind.Ftp => new FtpFileProvider(sp.GetRequiredService<AsyncFtpClient>(), location.RemotePath),
    LocationKind.GoogleDrive => new GoogleDriveFileProvider(
        sp.GetRequiredService<DriveService>(),
        location.FolderId),
    _ => throw new NotSupportedException($"Unsupported location kind: {location.Kind}")
});
```

## Adding a Custom Provider

1. Implement `IFileProvider` (and `IFileItem`, `IFolderItem` for your items).
2. Decide in your application how users select or address this provider.
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

# GitHub provider: unit and contract tests run against an in-memory GitHub; the live test reads a public repo
dotnet test tests/FileVault.GitHub.Tests --filter "Category!=Integration"
GITHUB_LIVE_TESTS=1 dotnet test tests/FileVault.GitHub.Tests --filter "Category=Integration"

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

GitHub Actions will pack and publish all packages to NuGet.org automatically, through NuGet Trusted
Publishing — no API key: a nuget.org trusted publishing policy for this repository and `publish.yml`,
and the `NUGET_USER` repository secret (nuget.org profile name of the policy's creator).

## License

[MIT](LICENSE)
