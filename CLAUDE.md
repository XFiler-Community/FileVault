# FileVault — Спецификация проекта

> **Назначение этого файла:** Полный старт для новой репы. Скопируй как `CLAUDE.md` в корень нового репозитория.
> Документ содержит только верифицированные решения из работающего кода X-Filer. Никаких выдуманных API.

---

## Контекст и мотивация

Проект является извлечением и обобщением файловой подсистемы X-Filer Desktop
(https://github.com/XFiler-Community/X-Filer.Desktop). Там реализованы и **проверены в рабочем коде**:
- `LocalFileProvider` — локальная ФС
- `YandexDiskFileProvider` — облачный провайдер через REST API

Цель: превратить эти контракты в **универсальную, платформо-независимую библиотеку NuGet-пакетов**,
пригодную для построения любого кроссплатформенного файлового менеджера (UI или CLI).

---

## Чего в этом проекте **нет** (жёсткие границы)

- Никакого UI-фреймворка (Avalonia, WPF, MAUI) в Core и провайдерах
- Никаких конкретных DI-фреймворков (Jab, Autofac) — только `Microsoft.Extensions.DependencyInjection.Abstractions`
- Никакого конкретного логгера — только `Microsoft.Extensions.Logging.Abstractions`
- `FileVault.Cli` — это **пример**, не production-tool
- Реализация Google Drive — **не в первой итерации** (слишком сложный OAuth flow)

---

## Структура пакетов

```
FileVault/
├── src/
│   ├── FileVault.Core/                   ← контракты, модели, хелперы. ТОЛЬКО BCL + M.E.Logging.Abstractions
│   ├── FileVault.Local/                  ← локальная ФС. Зависит от FileVault.Core
│   ├── FileVault.Ftp/                    ← FTP/FTPS через FluentFTP. Зависит от FileVault.Core
│   ├── FileVault.Sftp/                   ← SFTP через Renci.SshNet. Зависит от FileVault.Core
│   ├── FileVault.YandexDisk/             ← Яндекс Диск. Зависит от FileVault.Core
│   └── FileVault.Cli/                    ← консольный пример интеграции
├── tests/
│   ├── FileVault.Core.Tests/             ← unit-тесты хелперов и моделей
│   ├── FileVault.Local.Tests/            ← интеграционные тесты (реальная ФС в temp)
│   ├── FileVault.Ftp.Tests/              ← интеграционные (требуется FTP-сервер, см. ниже)
│   └── FileVault.Contract.Tests/        ← SHARED CONTRACT TESTS — главная ценность
└── Directory.Build.props                 ← TargetFramework=net9.0, Nullable=enable, ImplicitUsings=enable
```

### Зависимости (проверено существование на NuGet, версии уточнять при старте)

| Проект | Пакет | Зачем |
|---|---|---|
| Core | `Microsoft.Extensions.Logging.Abstractions` | `ILogger<T>` без конкретного логгера |
| Local | — | только BCL `System.IO` |
| Ftp | `FluentFTP` | AsyncFtpClient — реальный, проверенный клиент |
| Sftp | `Renci.SshNet` | SftpClient — реальный клиент |
| YandexDisk | `Egorozh.YandexDisk.Client` | кастомный клиент автора X-Filer, проверен |
| Все тесты | `NUnit`, `NSubstitute`, `Microsoft.NET.Test.Sdk` | стандартный стек |
| Ftp.Tests | `TestContainers` (NuGet: `Testcontainers`) | поднять FTP-сервер в Docker |

---

## Directory.Build.props (корневой)

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

---

## FileVault.Core — полные контракты

### IFileProviderItem.cs

```csharp
namespace FileVault.Core;

/// <summary>Базовый элемент файловой системы (файл или папка).</summary>
public interface IFileProviderItem
{
    /// <summary>Имя без пути (например "document.pdf" или "Downloads").</summary>
    string Name { get; }

    /// <summary>Полный путь или URI в рамках провайдера (например "C:\Users" или "disk:/Photos").</summary>
    string FullName { get; }

    bool IsHidden { get; }
    bool IsSystem { get; }

    DateTimeOffset ChangedDate { get; }

    /// <summary>Размер в байтах. null для папок или когда провайдер не возвращает размер.</summary>
    long? Size { get; }
}
```

### IFileItem.cs

```csharp
namespace FileVault.Core;

public interface IFileItem : IFileProviderItem
{
    string Extension { get; }           // ".pdf" (включая точку)
    string NameWithoutExtension { get; }

    /// <summary>Размер файла. Всегда определён для файлов (не папок).</summary>
    new long Size { get; }

    /// <summary>
    /// Открывает читающий поток. Владение потоком передаётся вызывающему — он обязан задиспозить.
    /// Возвращает (stream, totalBytes). totalBytes может быть 0 если длина неизвестна заранее.
    /// </summary>
    Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default);

    /// <summary>Удаляет файл на стороне провайдера (используется при cross-provider MoveIn).</summary>
    Task DeleteAsync(CancellationToken ct = default);
}
```

**Решение:** `Open()` (открыть файл в ОС) вынесен в отдельный `IOpenableItem` —
это платформенная ответственность, не файловая. Cloud-провайдеры не реализуют его.

### IOpenableItem.cs

```csharp
namespace FileVault.Core;

/// <summary>
/// Опциональный интерфейс. Только LocalFileProvider реализует его через Process.Start + UseShellExecute.
/// Cloud-провайдеры НЕ реализуют. Проверяй через `item is IOpenableItem openable`.
/// </summary>
public interface IOpenableItem
{
    void Open();
}
```

### IFolderItem.cs

```csharp
namespace FileVault.Core;

public interface IFolderItem : IFileProviderItem
{
    /// <summary>
    /// Создаёт провайдер для содержимого этой папки.
    /// Паттерн: FolderItem знает как создать Provider для себя
    /// (через замыкание на connection/api/credentials).
    /// </summary>
    IFileProvider CreateProvider();
}
```

### IDriveItem.cs

```csharp
namespace FileVault.Core;

public interface IDriveItem : IFolderItem
{
    long TotalSize { get; }
    long TotalFreeSpace { get; }
}
```

### IFileProvider.cs

Это **центральный контракт**. Все операции — с точки зрения **приёмника** (destination).
Источник данных — `IFileItem.OpenReadAsync()`.

```csharp
namespace FileVault.Core;

public interface IFileProvider
{
    string Name { get; }

    /// <summary>Полный путь/URI этого провайдера в рамках его пространства имён.</summary>
    string FullName { get; }

    // ── Перечисление содержимого ─────────────────────────────────────────────

    IAsyncEnumerable<IFileProviderItem> GetItemsAsync(FileProviderFilter filter,
        CancellationToken ct = default);

    // ── Создание ────────────────────────────────────────────────────────────

    Task<FileOperationResult<IFolderItem>> CreateFolderAsync(string name, CancellationToken ct = default);

    Task<FileOperationResult<IFileItem>> CreateEmptyTextFileAsync(string name, CancellationToken ct = default);

    // ── Удаление / Переименование ───────────────────────────────────────────

    Task<FileOperationResult<IReadOnlyList<IFileProviderItem>>> DeleteAsync(
        IReadOnlyList<IFileProviderItem> items, bool toRecycleBin, CancellationToken ct = default);

    Task<FileOperationResult<bool>> RenameAsync(IFileProviderItem item, string newName,
        CancellationToken ct = default);

    // ── Файловые операции (cross-provider safe) ─────────────────────────────

    /// <summary>
    /// Копирует файл В этот провайдер. Источник — любой IFileItem (даже из другого провайдера).
    /// Алгоритм: sourceItem.OpenReadAsync() → upload в провайдер.
    /// Same-provider провайдер может оптимизировать (server-side copy без скачивания).
    /// </summary>
    Task<FileOperationResult<IFileItem>> CopyFileInAsync(IFileItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default);

    /// <summary>
    /// Перемещает файл В этот провайдер.
    /// Same-drive/same-provider: нативный move без скачивания.
    /// Cross-provider: CopyFileIn + sourceItem.DeleteAsync().
    /// </summary>
    Task<FileOperationResult<IFileItem>> MoveFileInAsync(IFileItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default);

    // ── Папочные операции ────────────────────────────────────────────────────

    /// <summary>
    /// Копирует папку В этот провайдер.
    /// Same-provider: нативная копия через API (server-side, быстро).
    /// Cross-provider: ВОЗВРАЩАЕТ Result == null — оркестратор обходит дерево рекурсивно.
    /// Null — это НЕ ошибка, это сигнал "делай сам".
    /// </summary>
    Task<FileOperationResult<IFolderItem?>> CopyFolderInAsync(IFolderItem sourceItem, string destinationPath,
        IProgress<double> progress, CancellationToken ct = default);

    /// <summary>
    /// Перемещает папку внутри одного провайдера нативно.
    /// Cross-provider: возвращает Result == null — оркестратор делает CopyFolderIn + Delete.
    /// </summary>
    Task<FileOperationResult<IFolderItem?>> TryMoveFolderAsync(IFolderItem sourceItem,
        string destinationPath, IProgress<double> progress, CancellationToken ct = default);

    // ── Проверки существования (по имени В ТЕКУЩЕЙ ПАПКЕ провайдера) ─────────

    /// <param name="fileName">Только имя файла (не полный путь).</param>
    Task<bool> ContainsFileAsync(string fileName, CancellationToken ct = default);

    /// <param name="folderName">Только имя папки (не полный путь).</param>
    Task<bool> ContainsFolderAsync(string folderName, CancellationToken ct = default);

    // ── Разрешение пути назначения (оптимистичный UI) ────────────────────────

    /// <summary>
    /// Возвращает IFileItem с уже разрешённым FullName для операции copy/move.
    /// overwrite=false: если имя занято, автоматически добавляет " (2)", " (3)" и т.д.
    /// overwrite=true: возвращает путь с тем же именем (перезапись).
    /// Файл НЕ создаётся на диске — это только placeholder для UI.
    /// </summary>
    Task<IFileItem> ResolveDestinationFileAsync(IFileItem sourceItem, bool overwrite,
        CancellationToken ct = default);

    Task<IFolderItem> ResolveDestinationFolderAsync(IFolderItem sourceItem, bool overwrite,
        CancellationToken ct = default);
}
```

### IFileProviderResolver.cs

```csharp
namespace FileVault.Core;

/// <summary>
/// Создаёт IFileProvider по пути/URI и перечисляет корневые "диски" провайдера.
/// Каждый тип провайдера регистрирует свой Resolver в DI как IFileProviderResolver.
/// При нескольких зарегистрированных резолверах они опрашиваются по очереди.
/// </summary>
public interface IFileProviderResolver
{
    /// <summary>
    /// Возвращает null если этот резолвер не умеет обрабатывать данный route.
    /// LocalResolver обрабатывает "C:\", "/home/user".
    /// FtpResolver обрабатывает "ftp://host/path".
    /// YandexResolver обрабатывает "x-filevault:yandex-disk".
    /// </summary>
    Task<IFileProvider?> ResolveAsync(string route, CancellationToken ct = default);

    /// <summary>Корневые "диски" (буквы дисков, облачные корни, FTP-хосты).</summary>
    Task<IReadOnlyList<IDriveItem>> GetDrivesAsync(CancellationToken ct = default);

    /// <summary>Возвращает IFolderItem для пути (для навигации без полного провайдера).</summary>
    Task<IFolderItem?> GetFolderAsync(string route, CancellationToken ct = default);
}
```

### FileProviderFilter.cs

```csharp
namespace FileVault.Core;

public sealed class FileProviderFilter
{
    public bool ShowHiddenItems { get; init; }
    public bool ShowSystemItems { get; init; }

    public static FileProviderFilter Default { get; } = new();
    public static FileProviderFilter ShowAll { get; } = new() { ShowHiddenItems = true, ShowSystemItems = true };
}
```

### FileOperationResult.cs

```csharp
namespace FileVault.Core;

/// <summary>
/// Результат файловой операции. Используется для ошибок, которые показываются пользователю
/// (IO-ошибки, сетевые ошибки, нет прав). НЕ используется для программных ошибок —
/// те бросают обычные исключения.
/// </summary>
public sealed class FileOperationResult<T>
{
    public T? Result { get; init; }
    public Exception? Exception { get; init; }

    public bool IsSuccess => Exception is null;
    public string? ErrorMessage => Exception?.Message;

    public static FileOperationResult<T> Success(T result) => new() { Result = result };

    public static FileOperationResult<T> Failure(Exception ex) => new() { Exception = ex };

    public bool TryGetResult([NotNullWhen(true)] out T? result)
    {
        result = Result;
        return IsSuccess;
    }
}
```

### FileOperationsHelper.cs

```csharp
namespace FileVault.Core;

public static class FileOperationsHelper
{
    private const int DefaultBufferSize = 1024 * 1024; // 1 MB

    /// <summary>
    /// Копирует данные из sourceStream в destStream с репортингом прогресса.
    /// ВЛАДЕНИЕ СТРИМАМИ НЕ ПЕРЕДАЁТСЯ — вызывающий код диспозит сам.
    /// Если totalBytes == 0, прогресс не репортится до финала (неизвестная длина).
    /// </summary>
    public static async Task CopyAsync(
        Stream sourceStream,
        long totalBytes,
        Stream destStream,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        Memory<byte> buffer = new byte[DefaultBufferSize];
        long written = 0;
        int read;

        while ((read = await sourceStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await destStream.WriteAsync(buffer[..read], ct).ConfigureAwait(false);
            written += read;

            if (totalBytes > 0)
                progress?.Report((double)written / totalBytes);
        }

        progress?.Report(1.0);
    }
}
```

---

## Ключевые архитектурные решения (с обоснованием)

### 1. IProgress<double> вместо Action<double>

`IProgress<double>` — стандартный .NET паттерн (System namespace, .NET 4.5+).
Автоматически маршалит вызовы на UI-поток через `SynchronizationContext` если создан через `new Progress<double>(callback)`.
`Action<double>` требует ручного `Dispatcher.UIThread.Invoke(...)` в каждом потребителе.

### 2. Стримы: вызывающий диспозит

`FileOperationsHelper.CopyAsync` НЕ диспозит переданные стримы.
Правило: кто создал стрим — тот его и диспозит.
Паттерн в провайдерах:
```csharp
(var src, long len) = await sourceItem.OpenReadAsync(ct);
await using (src)
await using (var dst = CreateDestinationStream(...))
{
    await FileOperationsHelper.CopyAsync(src, len, dst, progress, ct);
}
```

### 3. Cross-provider операции

**Файлы:** `CopyFileInAsync` / `MoveFileInAsync` принимают любой `IFileItem`.
Алгоритм всегда работает: `OpenReadAsync()` → upload. Same-provider может сделать server-side copy.

**Папки:** `CopyFolderInAsync` возвращает `IFolderItem?`.
- `null` = "я не умею нативно, делай рекурсию сам"
- Same-provider провайдеры (Yandex, FTP) возвращают реальный результат.

**Кто делает рекурсию?** — Оркестратор (в UI/CLI слое). Алгоритм:
```
async RecursiveCopy(source, destProvider, destPath):
    folder = await destProvider.CreateFolderAsync(source.Name)
    subProvider = source.CreateProvider()
    items = subProvider.GetItemsAsync()
    for each fileItem: await destProvider.CopyFileInAsync(fileItem, ...)
    for each folderItem: RecursiveCopy(folderItem, subProvider, ...)
```

### 4. Пагинация в GetItemsAsync

`IAsyncEnumerable<IFileProviderItem>` — потребитель получает элементы по мере их получения.
Провайдер сам управляет батчами (FTP: по одному запросу, Yandex: limit/offset страницы).
Не нужен отдельный параметр пагинации в интерфейсе.

Пример для Yandex (limit/offset):
```csharp
public async IAsyncEnumerable<IFileProviderItem> GetItemsAsync(...)
{
    const int limit = 20;
    int offset = 0;
    while (true)
    {
        var page = await api.GetPageAsync(limit, offset, ct);
        if (page.Count == 0) yield break;
        foreach (var r in page) yield return MapItem(r);
        offset += page.Count;
        if (page.Count < limit) yield break; // последняя страница
    }
}
```

### 5. Разрешение имён при конфликте

`ResolveDestinationFileAsync` создаёт **placeholder** (объект с правильным FullName) для UI-оптимистичного добавления.
Файл НА ДИСКЕ не создаётся. Алгоритм выбора уникального имени:
- "file.pdf" → "file (2).pdf" → "file (3).pdf" → ...
- Проверка через `ContainsFileAsync(name)` или `File.Exists(path)`.

Локальный провайдер: синхронный `File.Exists` (достаточно быстро).
Yandex/FTP: асинхронный API-запрос, но только при реальном конфликте.

### 6. IFolderItem.CreateProvider()

Каждая реализация `IFolderItem` знает как создать `IFileProvider` для себя через замыкание:
```csharp
// FtpFolderItem замыкается на AsyncFtpClient
public IFileProvider CreateProvider() => new FtpFileProvider(_client, FullName);
```
Это избавляет от необходимости передавать connection через весь стек.
Недостаток: FolderItem держит ссылку на connection-объект. Это приемлемо.

### 7. Именование маршрутов

| Провайдер | Пример маршрута |
|---|---|
| Local (Windows) | `C:\Users\John` |
| Local (macOS/Linux) | `/home/john/Documents` |
| FTP | `ftp://ftp.example.com/public` |
| SFTP | `sftp://server.com/home/user` |
| Yandex Disk | `x-filevault:yandex-disk` (корень), `disk:/Photos` (подпапка) |

`IFileProviderResolver.ResolveAsync(route)` смотрит на префикс и возвращает `null` если не его.

---

## Реализация LocalFileProvider (проверенная база из X-Filer)

### Управление атрибутами (Windows/macOS)

```csharp
// Работает на обеих платформах:
var attrs = FileAttributes.None;
if (!showSystem) attrs |= FileAttributes.System;
if (!showHidden) attrs |= FileAttributes.Hidden;
var options = new EnumerationOptions { AttributesToSkip = attrs };
```

### Определение "того же диска" для нативного MoveFileIn

```csharp
private static bool OnSameDrive(string path1, string path2) =>
    string.Equals(
        Path.GetPathRoot(path1),
        Path.GetPathRoot(path2),
        StringComparison.OrdinalIgnoreCase);
```

На Windows: `C:\` == `C:\`. На macOS/Linux: `/` == `/` (всегда true, т.к. один корень).

### Рекурсивное копирование директории (LocalFileProvider.CopyFolderInAsync)

```csharp
private static async Task CopyDirectoryAsync(string source, string dest, CancellationToken ct)
{
    Directory.CreateDirectory(dest);

    foreach (var file in Directory.EnumerateFiles(source))
    {
        ct.ThrowIfCancellationRequested();
        await using var src = File.OpenRead(file);
        await using var dst = File.Create(Path.Combine(dest, Path.GetFileName(file)));
        await src.CopyToAsync(dst, ct);
    }

    foreach (var dir in Directory.EnumerateDirectories(source))
    {
        ct.ThrowIfCancellationRequested();
        await CopyDirectoryAsync(dir, Path.Combine(dest, Path.GetFileName(dir)), ct);
    }
}
```

### SystemFileItem: placeholder-конструктор

```csharp
// Для ResolveDestinationFileAsync — файл ещё не существует на диске
public SystemFileItem(string filePath)
{
    _fileInfo = new FileInfo(filePath);
    IsSystem = false;      // ← ВАЖНО: не true, это ещё не созданный файл
    IsHidden = false;
    ChangedDate = DateTimeOffset.Now;
    Size = 0;
}
```

### IFileOperations (опциональная зависимость для Recycle Bin)

В X-Filer используется пакет `XFiler.FileOperations.Abstractions` / `XFiler.FileOperations.Windows`.
В FileVault можно аналогично иметь `FileVault.Local.RecycleBin` как опциональный sub-пакет.
Без него `Delete(toRecycleBin: true)` работает как `toRecycleBin: false`.

---

## Скелет FTP-провайдера (FileVault.Ftp)

**Важно:** FluentFTP (`FluentFTP` на NuGet) — реальный пакет с `AsyncFtpClient`.
Перед реализацией **проверь актуальные сигнатуры в официальной документации FluentFTP на GitHub**
(https://github.com/robinrodricks/FluentFTP). API стабилен, но детали методов обновляются.

Ключевые методы AsyncFtpClient которые понадобятся:
- `ConnectAsync(ct)` / `DisconnectAsync(ct)`
- `GetListingAsync(path, ct)` → `FtpListItem[]`
- `UploadStreamAsync(stream, remotePath, FtpRemoteExists, ct)` → `FtpStatus`
- `DownloadStreamAsync(outStream, remotePath, ct)` → `bool`
- `DeleteFileAsync(path, ct)`
- `DeleteDirectoryAsync(path, ct)`
- `CreateDirectoryAsync(path, ct)`
- `MoveFileAsync(fromPath, toPath, FtpRemoteExists, ct)` → `bool`
- `MoveDirectoryAsync(fromPath, toPath, FtpRemoteExists, ct)` → `bool`
- `GetObjectInfoAsync(path, ct)` → `FtpListItem?`

`FtpListItem`:
- `.Type` — `FtpObjectType.File` / `FtpObjectType.Directory`
- `.Size` — `long`
- `.Modified` — `DateTime`
- `.Name` — имя без пути
- `.FullName` — полный путь

Структура провайдера:
```csharp
// Credentials хранятся в FtpConnection, провайдер получает клиент через замыкание
public class FtpFileProvider(AsyncFtpClient client, string remotePath, ILogger logger) : IFileProvider
{
    public string Name => Path.GetFileName(remotePath.TrimEnd('/'));
    public string FullName => $"ftp://{client.Host}{remotePath}";

    public async IAsyncEnumerable<IFileProviderItem> GetItemsAsync(...)
    {
        var items = await client.GetListingAsync(remotePath, ct);
        foreach (var item in items)
        {
            if (item.Type == FtpObjectType.Directory)
                yield return new FtpFolderItem(item, client, logger);
            else if (item.Type == FtpObjectType.File)
                yield return new FtpFileItem(item, client, logger);
        }
    }

    // CopyFolderInAsync: для same-provider нет нативного copy в FTP протоколе
    // → ВСЕГДА возвращать null (cross-provider алгоритм)
    public Task<FileOperationResult<IFolderItem?>> CopyFolderInAsync(...)
    {
        return Task.FromResult(FileOperationResult<IFolderItem?>.Success(null));
    }

    // TryMoveFolderAsync: MoveDirectory нативно есть в FluentFTP
    public async Task<FileOperationResult<IFolderItem?>> TryMoveFolderAsync(...)
    {
        if (sourceItem is not FtpFolderItem ftpFolder || ftpFolder.Client != client)
            return FileOperationResult<IFolderItem?>.Success(null); // cross-provider

        bool ok = await client.MoveDirectoryAsync(ftpFolder.FullName, destinationPath, ..., ct);
        ...
    }
}
```

---

## Contract Tests (FileVault.Contract.Tests)

**Это главная ценность библиотеки.** Один набор тестов покрывает все провайдеры.

```csharp
/// <summary>
/// Абстрактный базовый класс. Каждый провайдер наследует и переопределяет CreateProvider.
/// Тесты гоняются против реальной реализации.
/// </summary>
[TestFixture]
public abstract class FileProviderContractTests
{
    protected IFileProvider Provider { get; private set; } = null!;

    /// <summary>Создать провайдер с пустой корневой директорией для тестов.</summary>
    protected abstract Task<IFileProvider> CreateProviderAsync();

    /// <summary>Создать файл в корне провайдера с указанным содержимым.</summary>
    protected abstract Task SeedFileAsync(string name, byte[] content);

    /// <summary>Создать папку в корне провайдера.</summary>
    protected abstract Task SeedFolderAsync(string name);

    /// <summary>Прочитать файл из корня провайдера (для проверки результатов).</summary>
    protected abstract Task<byte[]> ReadFileAsync(string name);

    /// <summary>Проверить существование файла (прямой доступ к backend).</summary>
    protected abstract Task<bool> FileExistsAsync(string name);

    [SetUp]
    public async Task BaseSetUp() => Provider = await CreateProviderAsync();

    [TearDown]
    public abstract Task TearDown();

    // ── Тесты (реализуются в базовом классе, гоняются всеми наследниками) ──

    [Test]
    public async Task GetItemsAsync_EmptyDirectory_ReturnsEmpty()
    {
        var items = await Provider.GetItemsAsync(FileProviderFilter.Default).ToListAsync();
        Assert.That(items, Is.Empty);
    }

    [Test]
    public async Task CreateFolderAsync_ReturnsItemAndCreatesDirectory()
    {
        var result = await Provider.CreateFolderAsync("TestFolder");
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Result!.Name, Is.EqualTo("TestFolder"));
        Assert.That(await Provider.ContainsFolderAsync("TestFolder"), Is.True);
    }

    [Test]
    public async Task CreateFolderAsync_NameConflict_AppendsIndex()
    {
        await SeedFolderAsync("Dup");
        var result = await Provider.CreateFolderAsync("Dup");
        Assert.That(result.Result!.Name, Is.EqualTo("Dup (2)"));
    }

    [Test]
    public async Task CopyFileInAsync_CopiesContent()
    {
        byte[] data = [1, 2, 3, 4, 5];
        await SeedFileAsync("source.bin", data);
        var items = await Provider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();
        var srcItem = items.OfType<IFileItem>().Single(i => i.Name == "source.bin");

        var destPath = /* provider-specific path for "copy.bin" in same dir */ GetDestPath("copy.bin");
        var result = await Provider.CopyFileInAsync(srcItem, destPath, new Progress<double>());

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(await ReadFileAsync("copy.bin"), Is.EqualTo(data));
        Assert.That(await FileExistsAsync("source.bin"), Is.True, "источник должен остаться");
    }

    [Test]
    public async Task MoveFileInAsync_RemovesSource()
    {
        await SeedFileAsync("move.txt", [42]);
        var items = await Provider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();
        var srcItem = items.OfType<IFileItem>().Single();

        var result = await Provider.MoveFileInAsync(srcItem, GetDestPath("moved.txt"), new Progress<double>());

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(await FileExistsAsync("move.txt"), Is.False, "источник должен быть удалён");
        Assert.That(await FileExistsAsync("moved.txt"), Is.True);
    }

    [Test]
    public async Task ContainsFileAsync_ExistingFile_ReturnsTrue()
    {
        await SeedFileAsync("exists.txt", []);
        Assert.That(await Provider.ContainsFileAsync("exists.txt"), Is.True);
    }

    [Test]
    public async Task ContainsFileAsync_MissingFile_ReturnsFalse()
    {
        Assert.That(await Provider.ContainsFileAsync("ghost.txt"), Is.False);
    }

    [Test]
    public async Task ResolveDestinationFileAsync_NoConflict_KeepsName()
    {
        var fake = new FakeFileItem("report.pdf");
        var dest = await Provider.ResolveDestinationFileAsync(fake, overwrite: false);
        Assert.That(dest.Name, Is.EqualTo("report.pdf"));
    }

    [Test]
    public async Task ResolveDestinationFileAsync_Conflict_AppendsIndex()
    {
        await SeedFileAsync("report.pdf", []);
        var fake = new FakeFileItem("report.pdf");
        var dest = await Provider.ResolveDestinationFileAsync(fake, overwrite: false);
        Assert.That(dest.Name, Is.EqualTo("report (2).pdf"));
    }

    [Test]
    public async Task RenameAsync_File_ChangesName()
    {
        await SeedFileAsync("old.txt", []);
        var items = await Provider.GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();
        var result = await Provider.RenameAsync(items.Single(), "new.txt");
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(await FileExistsAsync("new.txt"), Is.True);
        Assert.That(await FileExistsAsync("old.txt"), Is.False);
    }

    // helper — провайдер знает свой формат путей
    protected abstract string GetDestPath(string name);

    // минимальный fake для тестирования ResolveDestination
    protected sealed class FakeFileItem(string name) : IFileItem
    {
        public string Name { get; } = name;
        public string FullName => name;
        public bool IsHidden => false;
        public bool IsSystem => false;
        public DateTimeOffset ChangedDate => DateTimeOffset.Now;
        long? IFileProviderItem.Size => 0;
        public long Size => 0;
        public string Extension => Path.GetExtension(name);
        public string NameWithoutExtension => Path.GetFileNameWithoutExtension(name);
        public Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default)
            => Task.FromResult<(Stream, long)>((new MemoryStream(), 0));
        public Task DeleteAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
```

### Реализация для LocalProvider

```csharp
public class LocalFileProviderContractTests : FileProviderContractTests
{
    private string _tempDir = null!;

    protected override Task<IFileProvider> CreateProviderAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var resolver = new LocalFileProviderResolver(/* IFileOperations stub */);
        return resolver.ResolveAsync(_tempDir)!;
    }

    protected override Task SeedFileAsync(string name, byte[] content)
    {
        File.WriteAllBytes(Path.Combine(_tempDir, name), content);
        return Task.CompletedTask;
    }

    protected override Task SeedFolderAsync(string name)
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, name));
        return Task.CompletedTask;
    }

    protected override Task<byte[]> ReadFileAsync(string name)
        => Task.FromResult(File.ReadAllBytes(Path.Combine(_tempDir, name)));

    protected override Task<bool> FileExistsAsync(string name)
        => Task.FromResult(File.Exists(Path.Combine(_tempDir, name)));

    protected override string GetDestPath(string name) => Path.Combine(_tempDir, name);

    public override Task TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
        return Task.CompletedTask;
    }
}
```

### Реализация для FTP (через TestContainers)

```csharp
// Поднимает реальный FTP-сервер в Docker для интеграционных тестов.
// TestContainers (пакет Testcontainers на NuGet) — реальный пакет.
// Docker должен быть запущен на машине разработчика / CI.
[TestFixture]
[Category("Integration")] // можно скипать в быстрых тестах
public class FtpFileProviderContractTests : FileProviderContractTests
{
    private IContainer _ftpContainer = null!;
    private AsyncFtpClient _client = null!;

    [OneTimeSetUp]
    public async Task StartContainer()
    {
        // Конкретный образ и настройки уточнить при реализации.
        // Варианты: garethflowers/ftp-server, fauria/vsftpd, stilliard/pure-ftpd
        _ftpContainer = new ContainerBuilder()
            .WithImage("garethflowers/ftp-server") // проверить актуальный образ
            .WithPortBinding(21, true)
            // ... env vars для user/pass/passive ports
            .Build();
        await _ftpContainer.StartAsync();

        _client = new AsyncFtpClient(
            host: "localhost",
            port: _ftpContainer.GetMappedPublicPort(21),
            user: "test",
            pass: "test");
        await _client.ConnectAsync();
    }

    [OneTimeTearDown]
    public async Task StopContainer()
    {
        await _client.DisconnectAsync();
        await _ftpContainer.StopAsync();
    }

    protected override Task<IFileProvider> CreateProviderAsync()
    {
        string testRoot = $"/test_{Guid.NewGuid():N}";
        // создать папку через FTP клиент
        return Task.FromResult<IFileProvider>(new FtpFileProvider(_client, testRoot, NullLogger.Instance));
    }

    // ... остальные методы аналогично Local
}
```

---

## CLI-пример (FileVault.Cli)

Демонстрирует интеграцию без UI. Минимально:

```
filevault list <path>
filevault copy <source> <dest>
filevault move <source> <dest>
filevault mkdir <path> <name>
```

Пример команды `list`:
```csharp
// Program.cs
var resolver = new CompositeFileProviderResolver([
    new LocalFileProviderResolver(),
    new FtpFileProviderResolver(),
]);

var provider = await resolver.ResolveAsync(args[1]);
if (provider is null) { Console.Error.WriteLine("Unknown path"); return 1; }

await foreach (var item in provider.GetItemsAsync(FileProviderFilter.ShowAll))
{
    string size = item.Size.HasValue ? $"{item.Size:N0} bytes" : "<dir>";
    Console.WriteLine($"{item.Name,-40} {size}");
}
```

`CompositeFileProviderResolver` — простой класс в `FileVault.Core` (НЕ интерфейс),
который опрашивает список резолверов и возвращает первый ненулевой результат.

---

## DI-интеграция (рекомендуемый паттерн)

Не навязываем DI-фреймворк, но показываем как интегрироваться со стандартным
`Microsoft.Extensions.DependencyInjection`:

```csharp
// Extension methods в каждом пакете:
// FileVault.Local:
services.AddLocalFileProvider();

// FileVault.Ftp:
services.AddFtpFileProvider(options => {
    options.Host = "ftp.example.com";
    options.Username = "user";
    options.Password = "pass";
});

// Под капотом все регистрируют IFileProviderResolver.
// CompositeFileProviderResolver собирает их вместе:
services.AddSingleton<IFileProviderResolver>(sp =>
    new CompositeFileProviderResolver(sp.GetServices<IFileProviderResolver>()));
```

---

## Что НЕ делать (антипаттерны выученные на X-Filer)

1. **НЕ диспозить стримы в `CopyAsync`** — ownership у вызывающего кода
2. **НЕ ставить `async Task` без `await`** — лишний overhead (как было в `SystemFileItem.Remove`)
3. **НЕ смешивать `null` результат с ошибкой** — `CopyFolderInAsync` возвращает `null` как валидный "cross-provider" сигнал
4. **НЕ хардкодить `IsSystem=true, IsHidden=true`** у placeholder-элементов (ResolveDestination)
5. **НЕ проверять `item.Name` в `ContainsFileAsync`** — всегда комбинировать с текущим путём провайдера (был баг в Yandex)
6. **НЕ использовать `CopyAsync` вместо `MoveAsync`** в `TryMoveFolder` same-provider (был баг в Yandex)
7. **НЕ возвращать мёртвый код** (был `CreateOptions` который никогда не возвращал `null` но проверка была)
8. **НЕ дублировать `AreFilesOnSameDrive`/`AreFoldersOnSameDrive`** — одна функция `OnSameDrive(string, string)`

---

## Порядок реализации (рекомендуемый)

1. `FileVault.Core` — все интерфейсы + `FileOperationResult` + `FileOperationsHelper` + `FileProviderFilter`
2. `FileVault.Core.Tests` — unit-тесты `FileOperationResult` и `FileOperationsHelper`
3. `FileVault.Contract.Tests` — базовый класс `FileProviderContractTests` (без конкретных реализаций)
4. `FileVault.Local` — полная реализация (взять из X-Filer, убрать `IFileOperations` зависимость или сделать опциональной)
5. `FileVault.Local.Tests : FileProviderContractTests` — должны зеленеть все contract tests
6. `FileVault.Ftp` — по образцу Local, с реальным FluentFTP
7. `FileVault.Ftp.Tests : FileProviderContractTests` — с TestContainers
8. `FileVault.Cli` — пример интеграции
9. `FileVault.Sftp`, `FileVault.YandexDisk` — по образцу Ftp

---

## Репозиторий X-Filer как reference implementation

Рабочий код с исправленными багами находится в:
https://github.com/XFiler-Community/X-Filer.Desktop (ветка `develop`)

Ключевые файлы для изучения перед началом:
- `src/app/FileProviders/XFiler.FileProviders.Core/` — интерфейсы (прообраз FileVault.Core)
- `src/app/FileProviders/XFiler.FileProviders.Local/` — рабочая локальная реализация
- `src/app/FileProviders/XFiler.FileProviders.YandexDisk/` — рабочий cloud-провайдер
- `src/app/XFiler.Tests/` — тесты (30/30 зелёных)

Разница между X-Filer и FileVault:
- Имена методов: `CopyIn` → `CopyFileInAsync`, `FileContains` → `ContainsFileAsync` и т.д.
- `Action<double>` → `IProgress<double>`
- Нет Avalonia, Jab, Serilog в Core
- Все методы с суффиксом `Async` по конвенции .NET
