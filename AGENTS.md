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
  FileVault.YandexDisk/    ← Egorozh.YandexDisk.Client
  FileVault.GoogleDrive/   ← Google.Apis.Drive.v3
  FileVault.Wsl/           ← Windows-only; depends on FileVault.Local (not Core directly)
  FileVault.Cli/           ← example only; not a NuGet package
tests/
  FileVault.Contract.Tests/ ← abstract base class; no concrete tests
  FileVault.Core.Tests/
  FileVault.Local.Tests/
  FileVault.Ftp.Tests/
  FileVault.GoogleDrive.Tests/
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
   - `<Name>FileProviderResolver.cs`
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
| `CopyFileInAsync` | Always works cross-provider via `sourceItem.OpenReadAsync()` → upload. Same-provider may optimize server-side. |
| `MoveFileInAsync` | Same-drive/same-provider → native move. Cross-provider → `CopyFileInAsync` + `sourceItem.DeleteAsync()`. |
| `CopyFolderInAsync` | Return `Success(null)` if native folder copy not supported. `null` result is **not an error** — it is the signal for the orchestrator to recurse. FTP always returns `Success(null)`. |
| `TryMoveFolderAsync` | Return `Success(null)` for cross-provider. Use native API only for same-provider moves. |
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
await using (var dst = File.Create(destinationPath))
{
    await FileOperationsHelper.CopyAsync(src, len, dst, progress, ct);
}
```

### Placeholder items

Used when the provider API doesn't return full metadata after a write operation. Rules:
- `internal sealed` — never expose outside the provider assembly.
- `OpenReadAsync` → empty `MemoryStream`, `totalBytes = 0`.
- `DeleteAsync` → no-op.
- `CreateProvider()` (FolderItem) → provider for parent path as fallback.
- `IsHidden = false`, `IsSystem = false`, `Size = 0`, `ChangedDate = DateTimeOffset.Now`.

---

## Route formats

| Provider | Route | Notes |
|---|---|---|
| Local | Absolute rooted path | `C:\Users\alice`, `/home/bob` |
| FTP | `ftp://host/path` | Port optional |
| SFTP | `sftp://host/path` | Port optional |
| Yandex Disk | `x-filevault:yandex-disk` (root) · `disk:/path` (subfolder) | |
| Google Drive | `x-filevault:google-drive` (root) · `gdrive:<fileId>` (subfolder) | |
| WSL | `wsl://<distro>` · `wsl://<distro>/linux/path` | Windows only; maps to `\\wsl$\<distro>\` |

`ResolveAsync` must return `null` (not throw) for routes it does not own.

---

## IOpenableItem

Only `FileVault.Local` implements `IOpenableItem` (via `Process.Start` + `UseShellExecute = true`).  
Cloud and protocol providers do **not** implement it.  
Callers check with `item is IOpenableItem openable` before calling `Open()`.

---

## Windows-only providers

Providers that only function on Windows (currently `FileVault.Wsl`) must:
- Return `null` / empty list from all `IFileProviderResolver` methods when `!OperatingSystem.IsWindows()`.
- Never throw on other platforms.

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

- `ci.yml` — runs on push/PR to `main`: build (Release) + `FileVault.Core.Tests` + `FileVault.Local.Tests`.
- `publish.yml` — runs on `v*` git tags: pack all `IsPackable=true` projects + push to NuGet.org via `NUGET_API_KEY` secret.
- FTP and Google Drive integration tests are **not** run in CI (require Docker / service account).

---

## Anti-patterns (from production bugs in X-Filer)

- Don't dispose streams inside `FileOperationsHelper.CopyAsync` — ownership stays with the caller.
- Don't return `null` from `CopyFolderInAsync` as an error — `null` result means "recurse yourself".
- Don't set `IsSystem = true` or `IsHidden = true` on placeholder items.
- Don't check `ContainsFileAsync` against a global path — always scope to the current provider directory.
- Don't use `CopyAsync` in `TryMoveFolder` for same-provider moves — use the native move API.
- Don't duplicate `OnSameDrive` — one function `OnSameDrive(string path1, string path2)` per provider that needs it.
- Don't add `async Task` without an `await` — use `Task.FromResult` for synchronous returns.
