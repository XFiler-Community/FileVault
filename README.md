# FileVault

[![CI](https://github.com/XFiler-Community/FileVault/actions/workflows/ci.yml/badge.svg)](https://github.com/XFiler-Community/FileVault/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/FileVault.Core.svg)](https://www.nuget.org/packages/FileVault.Core)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Универсальная, платформо-независимая библиотека .NET для работы с файловыми системами. Единый интерфейс `IFileProvider` скрывает разницу между локальной ФС, FTP, SFTP и облачными хранилищами. Извлечена из файловой подсистемы [X-Filer Desktop](https://github.com/XFiler-Community/X-Filer.Desktop).

## Пакеты

| Пакет | NuGet | Описание |
|---|---|---|
| `FileVault.Core` | [![NuGet](https://img.shields.io/nuget/v/FileVault.Core.svg)](https://www.nuget.org/packages/FileVault.Core) | Контракты и хелперы. Зависит только от BCL |
| `FileVault.Local` | [![NuGet](https://img.shields.io/nuget/v/FileVault.Local.svg)](https://www.nuget.org/packages/FileVault.Local) | Локальная файловая система |
| `FileVault.Ftp` | [![NuGet](https://img.shields.io/nuget/v/FileVault.Ftp.svg)](https://www.nuget.org/packages/FileVault.Ftp) | FTP/FTPS через [FluentFTP](https://github.com/robinrodricks/FluentFTP) |
| `FileVault.Sftp` | [![NuGet](https://img.shields.io/nuget/v/FileVault.Sftp.svg)](https://www.nuget.org/packages/FileVault.Sftp) | SFTP через [SSH.NET](https://github.com/sshnet/SSH.NET) |
| `FileVault.YandexDisk` | [![NuGet](https://img.shields.io/nuget/v/FileVault.YandexDisk.svg)](https://www.nuget.org/packages/FileVault.YandexDisk) | Яндекс Диск через REST API |

## Установка

```shell
dotnet add package FileVault.Core
dotnet add package FileVault.Local
dotnet add package FileVault.Ftp
dotnet add package FileVault.Sftp
dotnet add package FileVault.YandexDisk
```

## Быстрый старт

### Локальная файловая система

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
    PrivateKeyPath = "~/.ssh/id_rsa",   // или Password
});

var provider = await resolver.ResolveAsync("sftp://ssh.example.com/var/data");
```

### Яндекс Диск

```csharp
using FileVault.YandexDisk;

var resolver = new YandexDiskFileProviderResolver(oauthToken: "y0_AgAAAA...");

// Корень диска
var provider = await resolver.ResolveAsync("x-filevault:yandex-disk");

// Конкретная папка
var photos = await resolver.ResolveAsync("disk:/Фотографии");
```

### Несколько провайдеров одновременно

`CompositeFileProviderResolver` опрашивает список резолверов и возвращает первый результат. Используется в точках интеграции (UI, CLI).

```csharp
using FileVault.Core;
using FileVault.Ftp;
using FileVault.Local;

var resolver = new CompositeFileProviderResolver([
    new LocalFileProviderResolver(),
    new FtpFileProviderResolver(new FtpConnection { Host = "ftp.example.com", ... }),
]);

// Работает для любого пути — локального или FTP
var provider = await resolver.ResolveAsync(path);
```

## Основные операции

Все операции атомарны с точки зрения провайдера. Успех/ошибка возвращаются через `FileOperationResult<T>` — исключения не летят в вызывающий код.

```csharp
// Создать папку (при конфликте имени добавит " (2)", " (3)" и т.д.)
var result = await provider.CreateFolderAsync("NewFolder");
if (result.IsSuccess)
    Console.WriteLine(result.Result!.FullName);

// Скопировать файл В этот провайдер из любого другого
var destPath = "/backups/report.pdf";
var copyResult = await destProvider.CopyFileInAsync(sourceFileItem, destPath, new Progress<double>(
    v => Console.Write($"\r{v:P0}")
));

// Переместить файл (same-drive — нативный move; cross-provider — copy+delete)
await destProvider.MoveFileInAsync(sourceFileItem, destPath, progress);

// Удалить
await provider.DeleteAsync([item], toRecycleBin: false);

// Переименовать
await provider.RenameAsync(item, "new-name.txt");

// Получить уникальный путь назначения (файл НЕ создаётся)
var dest = await provider.ResolveDestinationFileAsync(sourceFile, overwrite: false);
// "report.pdf" → "report (2).pdf" если оригинал уже существует
```

### Копирование папок между провайдерами

`CopyFolderInAsync` возвращает `null` в `Result` вместо ошибки, если провайдер не поддерживает нативное копирование (FTP, SFTP, cross-provider). Оркестратор должен выполнить рекурсию самостоятельно:

```csharp
async Task RecursiveCopyAsync(IFolderItem source, IFileProvider dest, string destPath)
{
    // Пробуем нативное копирование (Yandex: server-side; остальные: null)
    var result = await dest.CopyFolderInAsync(source, destPath, progress);
    if (result.IsSuccess && result.Result is not null)
        return; // готово

    // Нативного нет — рекурсия вручную
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

## Интеграция с Microsoft.Extensions.DependencyInjection

Каждый пакет регистрирует свой `IFileProviderResolver`. `CompositeFileProviderResolver` собирает их вместе.

```csharp
services.AddSingleton<IFileProviderResolver, LocalFileProviderResolver>();

services.AddSingleton<IFileProviderResolver>(_ =>
    new FtpFileProviderResolver(new FtpConnection
    {
        Host     = config["Ftp:Host"]!,
        Username = config["Ftp:Username"],
        Password = config["Ftp:Password"],
    }));

services.AddSingleton<IFileProviderResolver>(sp =>
    new CompositeFileProviderResolver(sp.GetServices<IFileProviderResolver>()));
```

## Добавление собственного провайдера

1. Реализуйте `IFileProvider` (и `IFileProviderItem`, `IFileItem`, `IFolderItem` для ваших элементов).
2. Реализуйте `IFileProviderResolver`.
3. Напишите тесты, наследуясь от `FileProviderContractTests` из пакета `FileVault.Contract.Tests`.

```csharp
// MyProvider.Tests.csproj
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

Все 17 контрактных тестов запустятся автоматически.

## Разработка

### Требования

- .NET 10 SDK
- Docker (для интеграционных тестов FTP через TestContainers)

### Сборка и тесты

```shell
dotnet build
dotnet test tests/FileVault.Core.Tests
dotnet test tests/FileVault.Local.Tests

# FTP integration tests (requires Docker)
dotnet test tests/FileVault.Ftp.Tests --filter "Category=Integration"
```

### Релиз

Версия задаётся через git-тег:

```shell
git tag v1.2.0
git push origin v1.2.0
```

GitHub Actions запакует и опубликует все пакеты на NuGet.org автоматически.

## Лицензия

[MIT](LICENSE)
