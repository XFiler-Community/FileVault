# FileVault

[![CI](https://github.com/XFiler-Community/FileVault/actions/workflows/ci.yml/badge.svg)](https://github.com/XFiler-Community/FileVault/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/FileVault.Core.svg)](https://www.nuget.org/packages/FileVault.Core)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A platform-independent .NET library for working with file systems. The single `IFileProvider` interface abstracts away the differences between the local file system, FTP, SFTP, and cloud storage. Extracted from the file subsystem of [X-Filer Desktop](https://x-filer.com).

FileVault does not parse user-entered paths or choose a backend for an application. The client resolves its own routes, bookmarks, accounts, and credentials, then creates the appropriate `IFileProvider` directly.

> **Supported backends:** Local FS · FTP/FTPS · SFTP · WebDAV (Nextcloud, ownCloud, Synology, Yandex Disk WebDAV, …) · Yandex Disk · Google Drive · OneDrive (personal and work/school) · Dropbox · GitHub repositories (with Git LFS) · WSL via UNC paths (Windows)

## Packages

| Package | NuGet | Description |
|---|---|---|
| `FileVault.Core` | [![NuGet](https://img.shields.io/nuget/v/FileVault.Core.svg)](https://www.nuget.org/packages/FileVault.Core) | Contracts and helpers. Depends only on BCL |
| `FileVault.Local` | [![NuGet](https://img.shields.io/nuget/v/FileVault.Local.svg)](https://www.nuget.org/packages/FileVault.Local) | Local file system |
| `FileVault.Ftp` | [![NuGet](https://img.shields.io/nuget/v/FileVault.Ftp.svg)](https://www.nuget.org/packages/FileVault.Ftp) | FTP/FTPS via [FluentFTP](https://github.com/robinrodricks/FluentFTP) |
| `FileVault.Sftp` | [![NuGet](https://img.shields.io/nuget/v/FileVault.Sftp.svg)](https://www.nuget.org/packages/FileVault.Sftp) | SFTP via [SSH.NET](https://github.com/sshnet/SSH.NET) |
| `FileVault.WebDav` | [![NuGet](https://img.shields.io/nuget/v/FileVault.WebDav.svg)](https://www.nuget.org/packages/FileVault.WebDav) | Any WebDAV server over HttpClient (Basic auth), no extra dependencies, AOT-compatible |
| `FileVault.YandexDisk` | [![NuGet](https://img.shields.io/nuget/v/FileVault.YandexDisk.svg)](https://www.nuget.org/packages/FileVault.YandexDisk) | Yandex Disk via REST API |
| `FileVault.GoogleDrive` | [![NuGet](https://img.shields.io/nuget/v/FileVault.GoogleDrive.svg)](https://www.nuget.org/packages/FileVault.GoogleDrive) | Google Drive via Drive API v3 |
| `FileVault.Dropbox` | [![NuGet](https://img.shields.io/nuget/v/FileVault.Dropbox.svg)](https://www.nuget.org/packages/FileVault.Dropbox) | Dropbox via HTTP API v2 over HttpClient (AOT-safe, no SDK), with an OAuth 2 PKCE sign-in helper |
| `FileVault.OneDrive` | [![NuGet](https://img.shields.io/nuget/v/FileVault.OneDrive.svg)](https://www.nuget.org/packages/FileVault.OneDrive) | OneDrive via Microsoft Graph over plain HttpClient (NativeAOT-safe), with an OAuth PKCE sign-in helper |
| `FileVault.GitHub` | [![NuGet](https://img.shields.io/nuget/v/FileVault.GitHub.svg)](https://www.nuget.org/packages/FileVault.GitHub) | One branch of a GitHub repository via REST API: commits as writes, Git LFS reads, history |
| `FileVault.Wsl` | [![NuGet](https://img.shields.io/nuget/v/FileVault.Wsl.svg)](https://www.nuget.org/packages/FileVault.Wsl) | WSL distros via `\\wsl$\` UNC paths (Windows 10 1903+) |

## Installation

```shell
dotnet add package FileVault.Core
dotnet add package FileVault.Local
dotnet add package FileVault.Ftp
dotnet add package FileVault.Sftp
dotnet add package FileVault.WebDav
dotnet add package FileVault.YandexDisk
dotnet add package FileVault.GoogleDrive
dotnet add package FileVault.Dropbox
dotnet add package FileVault.OneDrive
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

### WebDAV

```csharp
using FileVault.WebDav;

// One account; the base URI is the root folder of the account
using var connection = new WebDavConnection
{
    BaseUri = new Uri("https://cloud.example.com/remote.php/dav/files/alice/"),  // Nextcloud
    // BaseUri = new Uri("https://webdav.yandex.ru/"),                            // Yandex Disk
    // BaseUri = new Uri("https://nas.local:5006/"),                              // Synology
    UserName = "alice",
    Password = appPassword,
    // HttpClient = sharedClient,   // optional, caller-owned; otherwise the connection makes its own (no timeout)
};

// Root with the account's quota (one PROPFIND; also checks address and credentials)
var drive = await connection.GetDriveAsync();
Console.WriteLine($"{drive.TotalFreeSpace} of {drive.TotalSize} bytes free");   // 0 when the server does not say

// A folder: relative to the base URI, or a FullName of this server
var photos = new WebDavFileProvider(connection, "Photos/2024");
Console.WriteLine(photos.FullName);   // webdavs://cloud.example.com/remote.php/dav/files/alice/Photos/2024
```

> `FullName` is `webdavs://host[:port]/path` for https and `webdav://host[:port]/path` for http; `path` is decoded
> (spaces, `#`, `%`, Cyrillic as is) and starts at the host root. Copies and moves between items of the same
> `WebDavConnection` run on the server (`COPY` / `MOVE`, `Overwrite: F`; an existing destination folder is merged by
> the caller). Uploads stream with a known length (a source of unknown length is buffered in a temp file first) to a
> temp name and are moved over the destination when complete. WebDAV has no trash: `toRecycleBin: true` fails with
> `NotSupportedException`. Only Basic authentication is supported — a server that offers Digest only fails with
> `NotSupportedException`; use https.

### Yandex Disk

```csharp
using XFiler.YandexDisk.Client.Http;
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

// A picture of a file without downloading it (Google Drive, Yandex Disk and Dropbox files implement IThumbnailItem)
await foreach (var item in root.GetItemsAsync(FileProviderFilter.Default))
{
    if (item is IThumbnailItem { HasThumbnail: true } pictured)
        await using (var thumbnail = await pictured.OpenThumbnailAsync(256)) { /* JPEG or PNG */ }
}
```

> **Authentication** is the caller's responsibility. Pass any `DriveService` — OAuth2 user, service account, or impersonation. See [Google Auth Library docs](https://developers.google.com/api-client-library/dotnet/guide/aaa_oauth).

### Dropbox

```csharp
using FileVault.Core;
using FileVault.Dropbox;

// Sign-in: OAuth 2 code flow with PKCE for a public client — the app key only, no secret — with offline access
var auth = new DropboxAuth("your-app-key", httpClient);

// a) Paste flow: no redirect URI; Dropbox shows a code the user pastes into the app
var request = auth.CreateAuthorizationRequest();
OpenBrowser(request.AuthorizeUri);
var tokens = await auth.ExchangeCodeAsync(request, pastedCode);

// b) Loopback flow: a redirect URI registered exactly (port and path) in the App Console
var loopback = auth.CreateAuthorizationRequest(new Uri("http://127.0.0.1:53682/dropbox"));
OpenBrowser(loopback.AuthorizeUri);
var code = DropboxAuth.ParseRedirect(loopback, uriTheListenerReceived);   // checks state, throws on access_denied
tokens = await auth.ExchangeCodeAsync(loopback, code);

// tokens: AccessToken (about 4 hours), RefreshToken (store it as a secret), ExpiresAt, AccountId.
// The token source refreshes before expiry and after a 401 expired_access_token, and hands new tokens back to store.
var tokenSource = new DropboxRefreshingTokenSource(auth, tokens, (fresh, ct) => SaveAsync(fresh, ct));
var client = new DropboxClient(tokenSource, httpClient);
// or: new DropboxClient(ct => myTokenStore.GetValidAccessTokenAsync(ct), httpClient)

var drive = await client.GetDriveAsync();          // dropbox:/ with TotalSize / TotalFreeSpace
var root = drive.CreateProvider();
var photos = client.CreateProvider("/Photos");     // FullName: dropbox:/Photos

// Search below a folder on Dropbox's side; public view-only links; thumbnails
await foreach (var found in photos.SearchAsync("beach")) { /* dropbox:/Photos/2024/beach.jpg */ }

if (file is IPublicLinkItem shareable)
{
    var link = await shareable.GetPublicLinkAsync();   // existing link returned as it is
    await shareable.RemovePublicLinkAsync();
}

if (file is IThumbnailItem { HasThumbnail: true } pictured)
    await using (var thumbnail = await pictured.OpenThumbnailAsync(256)) { /* JPEG */ }

// Sign out
await auth.RevokeAsync(tokenSource.Current.AccessToken);
```

> `FullName` is `dropbox:/` for the root and `dropbox:/<path>` below it, with Dropbox's display casing; Dropbox
> compares paths case-insensitively (`DropboxPath.AreSame`). A full name does not name the account — an app with
> several accounts keeps that next to it. Items of one `DropboxClient` count as the same provider: copies and moves
> between them run on Dropbox (`copy_v2` / `move_v2`, a batch job past 10 000 entries). Uploads go in one
> `files/upload` request up to `SimpleUploadLimit` (150 MB; give the `HttpClient` a timeout long enough for that, or
> lower the limit) and through an upload session in `UploadChunkSize` chunks (8 MB) above it; Dropbox commits the
> file at the end, so a cancelled upload leaves nothing behind. Copying onto an existing file replaces it (the old
> content stays in Dropbox's version history or deleted files). **Delete:** `toRecycleBin: true` is `delete_v2` —
> restorable for the account's retention period; `toRecycleBin: false` is `permanently_delete`, which Dropbox allows
> Dropbox Business apps only: elsewhere the delete fails and nothing else is tried. 429 `Retry-After` is waited out up
> to `MaxRateLimitRetries` times (at most `MaxRetryAfter` each).
>
> **App Console:** permission type *Scoped access*; scopes `files.metadata.read`, `files.content.read`,
> `files.content.write`, `sharing.read`, `sharing.write` (links), `account_info.read` (quota, always on),
> `files.permanent_delete` only for Business apps. PKCE needs no app secret in the app. Register every loopback
> redirect URI exactly, port included; the paste flow needs none.
### OneDrive

```csharp
using FileVault.Core;
using FileVault.OneDrive;

// Sign-in (once per account): OAuth 2.0 authorization code + PKCE, public client — the app's own Entra client id,
// no secret. Register "Mobile and desktop applications" with redirect URI http://localhost (any port works).
var auth = new OneDriveAuth(httpClient, clientId: "<your-app-client-id>");   // authority "common"
var request = auth.CreateAuthorizationRequest(new Uri($"http://localhost:{port}/"));
// open request.Url in the system browser, wait for the redirect on the loopback listener, then:
var code = OneDriveAuth.GetAuthorizationCode(request, callbackUri);        // checks state, throws on error
OneDriveTokens tokens = await auth.RedeemCodeAsync(request, code);         // access, refresh, expiry
// store tokens.RefreshToken securely; later: await auth.RefreshAsync(refreshToken) — it rotates

// Tokens kept fresh (refresh before expiry and after a 401); persist the rotated refresh token
var tokenSource = new OneDriveTokenSource(auth, tokens);
tokenSource.TokensRefreshed += (_, t) => secureStore.Save(t.RefreshToken);

// Any token source works: the client asks it again with forceRefresh: true after Graph answers 401
var client = new OneDriveClient(httpClient, tokenSource.GetAccessTokenAsync);

var drive = await client.GetDriveAsync();          // onedrive:root, quota in TotalSize / TotalFreeSpace
var root = drive.CreateProvider();
var folder = client.CreateProvider(folderId);     // onedrive:<item-id>
var notes = await root.FindAsync("Documents/notes.md");

// Find below a folder on the server (ISearchableFileProvider), share a public view link (IPublicLinkItem)
await foreach (var hit in root.SearchAsync("budget")) Console.WriteLine(hit.FullName);
if (notes is IPublicLinkItem shareable)
{
    var link = await shareable.GetPublicLinkAsync();   // anyone with the link can view
    await shareable.RemovePublicLinkAsync();
}
```

> Full names: `onedrive:root`, `onedrive:<item-id>` (stable across rename and move), and `onedrive:<parent-id>/<name>`
> for a destination. Files over 4 MB go through an upload session (10 MiB fragments, resumable, cancelled sessions are
> discarded); uploads from another provider replace a file of the same name, since the caller resolved the name first.
> Copy and move within one `OneDriveClient` stay on the server. `toRecycleBin: true` uses the OneDrive recycle bin;
> `false` is `permanentDelete`, and a drive that refuses it returns `NotSupportedException` without deleting anything.
> 429/503 answers are retried after `Retry-After`. Files implement `IThumbnailItem`. Folders shared from other people's
> drives are not listed.

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
using FileVault.WebDav;
using FileVault.Dropbox;
using FileVault.OneDrive;

IFileProvider provider = location.Kind switch
{
    LocationKind.Local => new LocalFileProvider(location.Path),
    LocationKind.Ftp => new FtpFileProvider(ftpClient, location.RemotePath),
    LocationKind.WebDav => new WebDavFileProvider(webDavConnection, location.RemotePath),
    LocationKind.GoogleDrive => new GoogleDriveFileProvider(driveService, location.FolderId),
    LocationKind.Dropbox => dropboxClients[location.AccountId].CreateProvider(location.Path),
    LocationKind.OneDrive => oneDriveClient.CreateProvider(location.FolderId),
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
// Google Drive, OneDrive, Dropbox's deleted files; GitHub deletes with a commit the history can undo); other
// providers (FTP, SFTP, WebDAV) return Failure(NotSupportedException) and delete nothing.
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

`CopyFolderInAsync` returns `null` in `Result` — not an error — when the provider does not support native folder copying (FTP, SFTP, WebDAV onto an existing folder, or cross-provider scenarios). The orchestrator is expected to recurse manually:

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

### Search and public links (optional capabilities)

Two optional contracts in `FileVault.Core`; check for them with a type test. Both are implemented by the Yandex Disk and Google Drive providers and their file and folder items.

```csharp
// Find files and folders whose name contains the text, in this folder and every folder below it
if (provider is ISearchableFileProvider searchable)
{
    await foreach (var found in searchable.SearchAsync("report"))
        Console.WriteLine(found.FullName);
}

// A view-only link anyone can open without signing in (this changes sharing: ask the user first)
if (item is IPublicLinkItem shareable)
{
    var link = await shareable.GetPublicLinkAsync();   // existing link, or a new one
    if (link.IsSuccess)
        Console.WriteLine(link.Result);

    await shareable.RemovePublicLinkAsync();           // stop sharing; no link is not an error
}
```

| | `ISearchableFileProvider` | `IPublicLinkItem` |
|---|---|---|
| Yandex Disk | No search endpoint: pages through the flat list of every file on the disk (1000 per request) and keeps names below the folder. Folders are found through the files inside them — a folder with no file anywhere below it is not found | Publishes the resource and returns its `public_url`; remove = unpublish |
| Google Drive | Drive's `name contains` (matches the start of words: "report" finds "Annual report.pdf", not "myreport.pdf"), not trashed, kept to items below the folder; for `root` — My Drive, without items only shared with you. Shared drives are not searched | Adds an `anyone` / `reader` permission and returns `webViewLink`; an existing `anyone` permission is kept as is; remove = delete every `anyone` permission |

## Microsoft.Extensions.DependencyInjection

Register authenticated clients, services, or app-specific factories. FileVault itself does not provide a DI routing abstraction.

```csharp
services.AddSingleton(driveService);
services.AddSingleton(ftpClient);
services.AddSingleton(webDavConnection);   // WebDavConnection: one per account, disposes its own HttpClient
services.AddSingleton(sp => new DropboxClient(sp.GetRequiredService<IDropboxTokenSource>(), sp.GetRequiredService<HttpClient>()));
services.AddSingleton(sp => new OneDriveClient(sp.GetRequiredService<HttpClient>(),
    sp.GetRequiredService<OneDriveTokenSource>().GetAccessTokenAsync));

services.AddTransient<Func<SavedLocation, IFileProvider>>(sp => location => location.Kind switch
{
    LocationKind.Local => new LocalFileProvider(location.Path),
    LocationKind.Ftp => new FtpFileProvider(sp.GetRequiredService<AsyncFtpClient>(), location.RemotePath),
    LocationKind.WebDav => new WebDavFileProvider(sp.GetRequiredService<WebDavConnection>(), location.RemotePath),
    LocationKind.GoogleDrive => new GoogleDriveFileProvider(
        sp.GetRequiredService<DriveService>(),
        location.FolderId),
    LocationKind.Dropbox => sp.GetRequiredService<DropboxClient>().CreateProvider(location.Path),
    LocationKind.OneDrive => sp.GetRequiredService<OneDriveClient>().CreateProvider(location.FolderId),
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
- [rclone](https://rclone.org) (for WebDAV integration tests: `rclone serve webdav` on a temp folder; set `RCLONE_PATH` if it is not on PATH)

### Build & Test

```shell
dotnet build
dotnet test tests/FileVault.Core.Tests
dotnet test tests/FileVault.Local.Tests

# FTP integration tests (requires Docker)
dotnet test tests/FileVault.Ftp.Tests --filter "Category=Integration"

# WebDAV provider: unit tests run anywhere; contract and integration tests start `rclone serve webdav` (skipped without rclone)
dotnet test tests/FileVault.WebDav.Tests --filter "Category!=Integration"
dotnet test tests/FileVault.WebDav.Tests

# GitHub provider: unit and contract tests run against an in-memory GitHub; the live test reads a public repo
dotnet test tests/FileVault.GitHub.Tests --filter "Category!=Integration"
GITHUB_LIVE_TESTS=1 dotnet test tests/FileVault.GitHub.Tests --filter "Category=Integration"

# Dropbox provider: contract and unit tests against an in-memory Dropbox; the live test reads a real account
dotnet test tests/FileVault.Dropbox.Tests --filter "Category!=Integration"
DROPBOX_ACCESS_TOKEN=sl.... dotnet test tests/FileVault.Dropbox.Tests --filter "Category=Integration"
# OneDrive provider: unit and contract tests run against an in-memory Microsoft Graph; the live test only reads
dotnet test tests/FileVault.OneDrive.Tests --filter "Category!=Integration"
ONEDRIVE_ACCESS_TOKEN=eyJ0... dotnet test tests/FileVault.OneDrive.Tests --filter "Category=Integration"

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
