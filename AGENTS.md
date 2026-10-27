# Agent Rules for FileVault

Authoritative rules for AI agents working in this repository. Follow these exactly — they reflect decisions already embedded in the codebase.

---

## Project layout

```
src/
  FileVault.Core/          ← contracts + helpers (no external deps except M.E.Logging.Abstractions)
  FileVault.Local/         ← System.IO, no extra packages
  FileVault.Ftp/           ← FluentFTP
  FileVault.Sftp/          ← SSH.NET (Renci.SshNet)
  FileVault.YandexDisk/    ← XFiler.YandexDisk.Client
  FileVault.GoogleDrive/   ← Google.Apis.Drive.v3
  FileVault.GitHub/        ← GitHub REST + Git LFS batch API over HttpClient; no extra packages, AOT-safe JSON
  FileVault.OneDrive/      ← Microsoft Graph over HttpClient (no Graph SDK / MSAL), AOT-safe JSON; OAuth PKCE helper
  FileVault.Wsl/           ← Windows-only; depends on FileVault.Local (not Core directly)
  FileVault.Cli/           ← example only; not a NuGet package
tests/
  FileVault.Contract.Tests/ ← abstract base class; no concrete tests
  FileVault.Core.Tests/
  FileVault.Local.Tests/
  FileVault.Ftp.Tests/
  FileVault.GoogleDrive.Tests/
  FileVault.YandexDisk.Tests/  ← unit tests on a fake IDiskApi
  FileVault.GitHub.Tests/      ← contract + unit tests on an in-memory GitHub (FakeGitHub); live read test opt-in
  FileVault.OneDrive.Tests/    ← contract + unit tests on an in-memory Graph (FakeGraph); live read test opt-in
```

---

## Build config (Directory.Build.props)

- `TargetFramework`: net10.0
- `Nullable`: enable — **all code must be null-safe**
- `LangVersion`: latest
- `ImplicitUsings`: enable
- `TreatWarningsAsErrors`: false
- Versioning: MinVer from git tags (e.g. `v1.2.0` → package version `1.2.0`)

---

## Adding a new provider — checklist

Do all of these in one session:

1. **Create `src/FileVault.<Name>/`** with these files:
   - `FileVault.<Name>.csproj`
   - `<Name>FileProvider.cs`
   - `<Name>FileItem.cs`
   - `<Name>FolderItem.cs`
   - `<Name>PlaceholderFileItem.cs` (internal sealed)
   - `<Name>PlaceholderFolderItem.cs` (internal sealed)
   - `<Name>DriveItem.cs` (only if the provider has a concept of root volumes/accounts)

2. **Register in `FileVault.slnx`** under the `/src/` folder.

3. **Create `tests/FileVault.<Name>.Tests/`** that inherits `FileProviderContractTests`.  
   Tag with `[Category("Integration")]` if tests require external infrastructure (Docker, credentials, OS feature).

4. **Register test project in `FileVault.slnx`** under the `/tests/` folder.

5. **Update `README.md`** — see [README maintenance](#readme-maintenance) below.

---

## csproj conventions

**Provider package:**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>true</IsPackable>
    <Description>...</Description>
    <PackageTags>file-manager;file-provider;[provider-tag];cross-platform</PackageTags>
  </PropertyGroup>
  <ItemGroup>
    <None Include="..\..\README.md" Pack="true" PackagePath="\" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\FileVault.Core\FileVault.Core.csproj" />
    <!-- external NuGet deps here -->
  </ItemGroup>
</Project>
```

**Test project:**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\FileVault.<Name>\FileVault.<Name>.csproj" />
    <ProjectReference Include="..\FileVault.Contract.Tests\FileVault.Contract.Tests.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" ... />
    <PackageReference Include="NUnit" ... />
    <PackageReference Include="NUnit3TestAdapter" ... />
    <PackageReference Include="NUnit.Analyzers" ... />
    <PackageReference Include="coverlet.collector" ... />
  </ItemGroup>
</Project>
```

Global properties from Directory.Build.props apply — don't repeat TargetFramework or Nullable.

---

## Coding conventions

- All classes are `sealed` unless inheritance is required by design.
- Private fields: `_camelCase`.
- Connection/config objects use `init`-only properties.
- `ConfigureAwait(false)` on every `await` in library code (not in test code).
- `CancellationToken ct = default` on every async signature.
- Add `[System.Runtime.CompilerServices.EnumeratorCancellation]` to `ct` in `IAsyncEnumerable` methods.
- Never use `async Task` without an `await` — use `Task.FromResult` instead.
- No comments explaining *what* the code does. Comments only for non-obvious *why* (hidden constraint, workaround, invariant).

---

## Provider pattern — rules

### IFileProvider methods

| Method | Rule |
|---|---|
| `GetItemsAsync` | Stream via `yield return`. Folders first, then files. Apply `FileProviderFilter` via `EnumerationOptions.AttributesToSkip` (local) or manual check (cloud). |
| `CreateFolderAsync` | Resolve name conflict before creating (append ` (2)`, ` (3)`, …). |
| `CopyFileInAsync` | Always works cross-provider via `sourceItem.OpenReadAsync()` → upload. Same-provider may optimize server-side. Write through a temp file — see [Partial files](#partial-files). Pass `ct` into every read/write so cancellation stops the transfer mid-file. |
| `MoveFileInAsync` | Same-drive/same-provider → native move. Cross-provider → `CopyFileInAsync` + `sourceItem.DeleteAsync()`. Source path == destination on the same provider → no-op. |
| `CopyFolderInAsync` | Return `Success(null)` if native folder copy not supported. `null` result is **not an error** — it is the signal for the orchestrator to recurse. FTP always returns `Success(null)`. |
| `TryMoveFolderAsync` | Return `Success(null)` for cross-provider. Use native API only for same-provider moves. |
| `DeleteAsync` | `toRecycleBin: true` → backend trash. No trash → `Failure(NotSupportedException)` before touching any item; **never** silently delete permanently. Local gets its trash via optional `ILocalRecycleBin`. |
| `ContainsFileAsync` | Check by name **within the provider's current directory** — do not check a global path. |
| `ResolveDestinationFileAsync` | Return a placeholder with the correct `FullName`; do **not** create the file on disk. Use the `{baseName} ({index}){ext}` pattern starting at index 2. |

### Error handling — non-negotiable

Every `IFileProvider` method wraps its body in `try/catch(Exception ex)` and returns `FileOperationResult<T>.Failure(ex)`. Exceptions **never propagate** from provider methods to callers. Programming errors (null args, wrong state) are the only exception — those throw normally.

```csharp
public Task<FileOperationResult<IFolderItem>> CreateFolderAsync(string name, CancellationToken ct = default)
{
    try
    {
        // ...
        return Task.FromResult(FileOperationResult<IFolderItem>.Success(item));
    }
    catch (Exception ex)
    {
        return Task.FromResult(FileOperationResult<IFolderItem>.Failure(ex));
    }
}
```

### Stream ownership

`FileOperationsHelper.CopyAsync` does **not** dispose streams. The caller opens and disposes both source and destination streams:

```csharp
(var src, long len) = await sourceItem.OpenReadAsync(ct);
await using (src)
{
    await CopyToFileAsync(src, len, destinationPath, progress, ct); // opens and disposes the temp destination
}
```

### Partial files

A cancelled or failed copy must leave no partial destination file, and a failed Replace must keep the old content:

- Write to a temp file in the destination folder named `<name>.filevault-partial-<guid:N>` (drop `<name>` if it is over 200 chars), opened with `CreateNew`.
- Only after the copy completed, swap it in: Local — `File.Move(temp, destination, overwrite: true)`; SFTP — `posix-rename@openssh.com`, falling back to delete + rename when the server lacks it.
- On any exception delete the temp file (best effort, SFTP with its own short timeout since `ct` is already cancelled) and rethrow into the method's `Failure`.
- Same-folder temp keeps the final move a rename on the same volume. Backends that commit an upload atomically (Google Drive, Yandex Disk) don't need this.

### Placeholder items

Used when the provider API doesn't return full metadata after a write operation. Rules:
- `internal sealed` — never expose outside the provider assembly.
- `OpenReadAsync` → empty `MemoryStream`, `totalBytes = 0`.
- `DeleteAsync` → no-op.
- `CreateProvider()` (FolderItem) → provider for parent path as fallback.
- `IsHidden = false`, `IsSystem = false`, `Size = 0`, `ChangedDate = DateTimeOffset.Now`.

---

## Provider identity formats

FileVault providers expose `FullName` values, but user-entered path parsing and backend selection belong to the client application. Do not add a shared resolver layer to this library.

| Provider | FullName convention | Notes |
|---|---|---|
| Local | Absolute rooted path | `C:\Users\alice`, `/home/bob` |
| FTP | `ftp://host/path` | Port optional |
| SFTP | `sftp://host/path` | Port optional |
| Yandex Disk | `disk:/` (root) · `disk:/path` (subfolder) | |
| Google Drive | `gdrive:root` (root) · `gdrive:<fileId>` (subfolder) | |
| GitHub | `github:<owner>/<repo>@<branch>:/<path>` | Branch may hold `/`, never `:`; `GitHubPath.TryParse` refuses `..` and `.git` |
| OneDrive | `onedrive:root` (root) · `onedrive:<item-id>` (any item) · `onedrive:<parent-id>/<name>` (destination) | Ids survive rename/move; one `OneDriveClient` = one account's `/me/drive` |
| WSL | `\\wsl$\<distro>\` | Windows only; use `FileVault.Local` against the UNC path |

Clients decide whether to use these strings, their own saved-location model, or another route format.

---

## GitHub provider — decisions

- One `GitHubRepository` = one branch. Items and providers from the same instance are "same provider" (`ReferenceEquals(item.Repository, repository)`), so copy / move / rename / folder copy stay server-side as tree entries.
- Every write is **one commit**: blobs → tree on `base_tree` → commit → `PATCH` the ref with `force: false`. Changes are resolved against the head they land on; when the ref update is refused (someone pushed meanwhile) the same changes are applied on the new head, up to 3 times, then `GitHubConcurrencyException`. Never force-push.
- `WriteFileAsync(path, content, expectedBlobSha, message)` is the edit API: it writes nothing and returns `ChangedMeanwhile` when the blob at `path` is not `expectedBlobSha` (null — the file must not exist).
- `CommitAsync(edits, message)` commits several files at once — `GitHubFileEdit.Write` (content), `.Restore` (an entry of an earlier snapshot: its blob is reused, so nothing is uploaded and an LFS pointer stays a pointer), `.Delete` — each with the blob it must have at the head (null — no file). Any mismatch writes nothing and returns `ChangedMeanwhile` with those paths; the check runs again on every head tried.
- `RevertAsync(commitSha)` = `DiffAsync(first parent, commit)` turned into `Restore` / `Delete` edits, one commit, message `Revert "<title>"` like git. No merging: a file a later commit changed again makes it `ChangedMeanwhile` (nothing written); files already back are skipped, so a second revert is `NothingToCommit`. `DiffAsync` descends only into trees whose sha differs.
- Git keeps no empty folders: `CreateFolderAsync` commits `<folder>/.gitkeep`. Deleting a folder nulls every blob under it (recursive tree), never the tree entry itself.
- `toRecycleBin: true` deletes with a commit: the history is the trash.
- Git LFS: tree sizes of LFS files are pointer sizes, so `ListAsync` reads the tiny pointer blobs (once per sha) for the real size; `OpenReadAsync` replaces a pointer with the object via the batch API (token as basic-auth password; the signed storage URL gets only LFS's headers). Writing a path matched by the root `.gitattributes` `filter=lfs` is refused.
- Trees and LFS pointers are cached by sha (immutable); the branch head is re-read after `HeadCacheDuration` and always before a write.
- `ListRecursiveAsync(folder)` reads a whole subtree with one `git/trees/{sha}?recursive=1` and caches every tree in the answer, so listings below it cost no request; a recursive walk that is already fully cached costs none either. When GitHub answers `truncated: true` it reads that level alone and each subfolder the same way. `resolveLfsSizes: false` skips the pointer reads (sizes stay pointer sizes) for whole-repository scans.
- `ChangedDate` = date of the listed head commit; per-file dates come from `GetCommitsAsync(path)`.

## OneDrive provider — decisions

- Raw `HttpClient` against `https://graph.microsoft.com/v1.0/me/drive`; JSON through `OneDriveJsonContext` (source
  generation). The app supplies tokens through `OneDriveAccessTokenProvider(forceRefresh, ct)`; a 401 asks it once more
  with `forceRefresh: true`. `OneDriveAuth` (auth code + PKCE, public client, authority `common`) and
  `OneDriveTokenSource` (refresh before expiry, `TokensRefreshed` with the rotated refresh token) are optional helpers.
- 429/503 (and 5xx for upload fragments / copy monitors) are retried after `Retry-After`, `MaxRetries` times, each
  wait capped by `MaxRetryDelay`.
- "Same provider" = `ReferenceEquals(item.Client, client)`. Copy is `POST /copy` + polling the monitor URL (no token);
  move/rename is one `PATCH`. A taken name cannot be copied or moved onto on the server (personal drives ignore
  `conflictBehavior` on copy), so a file falls back to download + upload with `replace` (+ recycle the source on move)
  and a folder returns `Success(null)`.
- Uploads: `PUT …:/name:/content` up to 4 MB, else an upload session in 320 KiB-multiple fragments (10 MiB), sent
  without the token. Cross-provider uploads use `conflictBehavior=replace` (the name was resolved before; overwrite
  kept it on purpose), `CreateFolderAsync` / `CreateEmptyTextFileAsync` use `fail`. A cancelled session is `DELETE`d.
  OneDrive commits only complete uploads, so no partial files.
- `DeleteAsync(toRecycleBin: true)` = `DELETE` (OneDrive recycle bin); `false` = `permanentDelete`, and a drive that
  refuses it gives `NotSupportedException` — never a silent move to the recycle bin.
- `remoteItem` entries (folders shared from other drives) are skipped: they are not addressable under `/me/drive`.
- Search: `items/{id}/search(q=…)`, results filtered to the folder's subtree (by `parentReference.path`, else by walking
  parent ids) since Graph does not document that a folder search stays inside the folder.
- Public link: the item's own (non-inherited) anonymous `view` link, else `createLink` view/anonymous; removing deletes
  those permissions only.

## IFileRevisionsItem

Optional, like `IOpenableItem`: a file whose provider keeps past states. Only `GoogleDriveFileItem` implements it
(`revisions.list` oldest first, `revisions.get?alt=media`); Google Workspace files return no revisions, since their
content cannot be downloaded. Yandex Disk has no file history in its public REST API, so it does not implement it.
`GoogleDriveFileProvider.FindAsync(relativePath)` finds an item by names below the provider's folder.

## IThumbnailItem

Optional: a file whose provider keeps a small picture of it. `HasThumbnail` is known from the listing (no request);
`OpenThumbnailAsync(size)` returns a buffered JPEG/PNG of about that size, or null when there is none.
- Google Drive: `thumbnailLink` is in the listing fields; the size is the link's `=s<px>` suffix. The link lives for
  hours: a 403/404/410 asks `files.get` for a fresh one and tries once more. Google Docs files have thumbnails too.
- Yandex Disk: the listing's `preview` link (size S, 150 px); its `size` query parameter is set to the nearest larger of
  S/M/L/XL/XXL/XXXL, and if that is refused the link is used as listed. Downloaded through `IFilesClient`, which adds
  the OAuth header.
- OneDrive: `HasThumbnail` is guessed from the listing (image/photo/video facets, Office/PDF/picture extensions) — work/school
  drives cannot `$expand=thumbnails` on children. `thumbnails/0/c{size}x{size}/content` (redirect to storage, no
  token), falling back to small/medium/large on 400; 404 → null.
- Local, FTP, SFTP, GitHub do not implement it: a client reads the file itself if it wants a picture.

## IOpenableItem

Only `FileVault.Local` implements `IOpenableItem` (via `Process.Start` + `UseShellExecute = true`).  
Cloud and protocol providers do **not** implement it.  
Callers check with `item is IOpenableItem openable` before calling `Open()`.

---

## Windows-only providers

Providers that only function on Windows (currently `FileVault.Wsl`) must:
- Guard Windows-only APIs with `OperatingSystem.IsWindows()`.
- Never throw on other platforms for platform discovery helpers.

---

## Contract tests

Every provider **must** have a test class that inherits `FileProviderContractTests` from `FileVault.Contract.Tests`. All 17 base tests run automatically.

Required overrides:

```csharp
protected override Task<IFileProvider> CreateProviderAsync();   // isolated empty root
protected override Task SeedFileAsync(string name, byte[] content);
protected override Task SeedFolderAsync(string name);
protected override Task<byte[]> ReadFileAsync(string name);
protected override Task<bool> FileExistsAsync(string name);
protected override string GetDestPath(string name);            // provider-specific full path
public    override Task TearDown();                            // delete all test artifacts
```

Tests that require external infrastructure (Docker, live API, WSL) must be tagged `[Category("Integration")]`.

---

## README maintenance

**Required on every change that adds, removes, or renames a provider package:**

1. Update the **Supported backends** tagline at the top.
2. Add/update a row in the **Packages** table (with NuGet badge).
3. Add/update the `dotnet add package` line in **Installation**.
4. Add/update a **Quick Start** subsection with a minimal code example.
5. Add/update the **DI** registration example if applicable.

Do all five in the same session as the code change.

---

## CI/CD

- `ci.yml` — runs on push/PR to `main`: build (Release) + `FileVault.Core.Tests` + `FileVault.Local.Tests` + non-integration `FileVault.GoogleDrive.Tests` (fake HTTP handler) + `FileVault.YandexDisk.Tests` (fake `IDiskApi`) + non-integration `FileVault.GitHub.Tests` (`FakeGitHub`) + non-integration `FileVault.OneDrive.Tests` (`FakeGraph`).
- `publish.yml` — runs on `v*` git tags: pack all `IsPackable=true` projects + push to NuGet.org via NuGet Trusted Publishing (no API key: a nuget.org policy for this repo and `publish.yml`, plus the `NUGET_USER` secret — the nuget.org profile name of the policy's creator).
- FTP and Google Drive integration tests are **not** run in CI (require Docker / service account).

---

## Anti-patterns (from production bugs in X-Filer)

- Don't dispose streams inside `FileOperationsHelper.CopyAsync` — ownership stays with the caller.
- Don't stream straight into `destinationPath` (`File.Create`, `OpenWrite`) — a cancelled copy leaves a truncated file and destroys the original on Replace; use [Partial files](#partial-files).
- Don't wrap synchronous SSH.NET calls in `Task.Run(..., ct)` — the token is only checked before start; use the `*Async` APIs of `SftpClient`/`SftpFileStream`.
- Don't return `null` from `CopyFolderInAsync` as an error — `null` result means "recurse yourself".
- Don't set `IsSystem = true` or `IsHidden = true` on placeholder items.
- Don't check `ContainsFileAsync` against a global path — always scope to the current provider directory.
- Don't use `CopyAsync` in `TryMoveFolder` for same-provider moves — use the native move API.
- Don't decide "same provider" by item type alone — compare the client/account instance the item was created with (`ReferenceEquals(item.Client, client)`); otherwise a native copy/move runs on the wrong server or account.
- Don't swallow errors in `Contains*Async` — only "not found" means `false`; a network error answered as `false` makes the caller reuse an existing name and the upload overwrites it.
- Don't duplicate `OnSameDrive` — one function `OnSameDrive(string path1, string path2)` per provider that needs it.
- Don't add `async Task` without an `await` — use `Task.FromResult` for synchronous returns.
