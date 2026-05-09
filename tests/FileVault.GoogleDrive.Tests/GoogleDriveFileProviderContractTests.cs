using FileVault.Contract.Tests;
using FileVault.Core;
using FileVault.GoogleDrive;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using DriveFile = Google.Apis.Drive.v3.Data.File;

namespace FileVault.GoogleDrive.Tests;

/// <summary>
/// Integration contract tests for Google Drive.
/// Requires a service-account JSON at the path set in GOOGLE_DRIVE_SERVICE_ACCOUNT_JSON env var.
/// Each test run creates an isolated folder in Drive and deletes it on teardown.
///
/// To run locally:
///   1. Create a Google Cloud project, enable Drive API, create a service account.
///   2. Download the service account key JSON.
///   3. Share a target Drive folder with the service account email (or use drive-wide delegation).
///   4. Set env var: GOOGLE_DRIVE_SERVICE_ACCOUNT_JSON=/path/to/sa.json
///   5. Run tests with category filter: --filter "Category=Integration"
/// </summary>
[TestFixture]
[Category("Integration")]
public class GoogleDriveFileProviderContractTests : FileProviderContractTests
{
    private DriveService _service = null!;
    private string _testFolderId = null!;

    [OneTimeSetUp]
    public async Task ConnectAsync()
    {
        var credentialsPath = Environment.GetEnvironmentVariable("GOOGLE_DRIVE_SERVICE_ACCOUNT_JSON");
        if (string.IsNullOrWhiteSpace(credentialsPath))
            Assert.Ignore("Set GOOGLE_DRIVE_SERVICE_ACCOUNT_JSON to run Google Drive integration tests.");

#pragma warning disable CS0618
        var credential = GoogleCredential
            .FromFile(credentialsPath)
            .CreateScoped(DriveService.Scope.Drive);
#pragma warning restore CS0618

        _service = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "FileVault.Tests"
        });
    }

    [OneTimeTearDown]
    public async Task DisconnectAsync()
    {
        _service?.Dispose();
        await Task.CompletedTask;
    }

    protected override async Task<IFileProvider> CreateProviderAsync()
    {
        // Isolated folder per test run
        var folderName = $"filevault-test-{Guid.NewGuid():N}";
        var metadata = new DriveFile
        {
            Name = folderName,
            MimeType = "application/vnd.google-apps.folder"
        };
        var request = _service.Files.Create(metadata);
        request.Fields = "id";
        var folder = await request.ExecuteAsync();
        _testFolderId = folder.Id;

        return new GoogleDriveFileProvider(_service, _testFolderId);
    }

    protected override async Task SeedFileAsync(string name, byte[] content)
    {
        using var ms = new MemoryStream(content);
        var metadata = new DriveFile { Name = name, Parents = [_testFolderId] };
        var upload = _service.Files.Create(metadata, ms, "application/octet-stream");
        await upload.UploadAsync();
    }

    protected override async Task SeedFolderAsync(string name)
    {
        var metadata = new DriveFile
        {
            Name = name,
            MimeType = "application/vnd.google-apps.folder",
            Parents = [_testFolderId]
        };
        await _service.Files.Create(metadata).ExecuteAsync();
    }

    protected override async Task<byte[]> ReadFileAsync(string name)
    {
        var fileId = await FindFileIdAsync(name);
        using var ms = new MemoryStream();
        await _service.Files.Get(fileId).DownloadAsync(ms);
        return ms.ToArray();
    }

    protected override async Task<bool> FileExistsAsync(string name)
    {
        var q = $"'{_testFolderId}' in parents and name = '{EscapeQuery(name)}' and mimeType != 'application/vnd.google-apps.folder' and trashed = false";
        var request = _service.Files.List();
        request.Q = q;
        request.Fields = "files(id)";
        request.PageSize = 1;
        var result = await request.ExecuteAsync();
        return result.Files?.Count > 0;
    }

    protected override string GetDestPath(string name) => $"gdrive:{_testFolderId}/{name}";

    public override async Task TearDown()
    {
        if (_testFolderId != null)
            await _service.Files.Delete(_testFolderId).ExecuteAsync();
    }

    private async Task<string> FindFileIdAsync(string name)
    {
        var q = $"'{_testFolderId}' in parents and name = '{EscapeQuery(name)}' and trashed = false";
        var request = _service.Files.List();
        request.Q = q;
        request.Fields = "files(id)";
        request.PageSize = 1;
        var result = await request.ExecuteAsync();
        return result.Files?.FirstOrDefault()?.Id
               ?? throw new FileNotFoundException($"File '{name}' not found in test folder.");
    }

    private static string EscapeQuery(string value) => value.Replace(@"\", @"\\").Replace("'", @"\'");
}
