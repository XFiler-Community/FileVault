using System.Net;
using System.Text;
using FileVault.Core;

namespace FileVault.WebDav.Tests;

/// <summary>FullName format, path resolution and HTTP behaviour against a canned handler (no server).</summary>
public class WebDavConnectionTests
{
    [Test]
    public void FullName_Https_IsWebdavs_WithPathAndPort()
    {
        using var connection = new WebDavConnection { BaseUri = new Uri("https://cloud.example.com:8443/remote.php/dav/files/al%20ice/") };

        Assert.Multiple(() =>
        {
            Assert.That(connection.GetFullName(), Is.EqualTo("webdavs://cloud.example.com:8443/remote.php/dav/files/al ice"));
            Assert.That(connection.GetFullName("Фото/a #1.jpg"),
                Is.EqualTo("webdavs://cloud.example.com:8443/remote.php/dav/files/al ice/Фото/a #1.jpg"));
            Assert.That(connection.GetFullName("/Фото/"), Is.EqualTo(connection.GetFullName("Фото")));
        });
    }

    [Test]
    public void FullName_Http_DefaultPort_IsWebdav()
    {
        using var connection = new WebDavConnection { BaseUri = new Uri("http://nas.local/") };
        Assert.That(connection.GetFullName("x"), Is.EqualTo("webdav://nas.local/x"));
        Assert.That(connection.GetFullName(), Is.EqualTo("webdav://nas.local/"));
    }

    [Test]
    public void Provider_AcceptsFullNameUrlOrRelativePath()
    {
        using var connection = new WebDavConnection { BaseUri = new Uri("https://h/dav/") };

        var fromRelative = new WebDavFileProvider(connection, "a b/c");
        var fromFullName = new WebDavFileProvider(connection, "webdavs://h/dav/a b/c");
        var fromUrl = new WebDavFileProvider(connection, "https://h/dav/a%20b/c/");

        Assert.That(fromRelative.ServerPath, Is.EqualTo("/dav/a b/c"));
        Assert.That(fromFullName.ServerPath, Is.EqualTo("/dav/a b/c"));
        Assert.That(fromUrl.ServerPath, Is.EqualTo("/dav/a b/c"));
        Assert.That(fromRelative.FullName, Is.EqualTo("webdavs://h/dav/a b/c"));
        Assert.That(new WebDavFileProvider(connection).Name, Is.EqualTo("h"));
    }

    [TestCase("webdavs://h/other")]
    [TestCase("webdavs://elsewhere/dav/x")]
    [TestCase("webdav://h/dav/x")]
    [TestCase("ftp://h/dav/x")]
    [TestCase("../x")]
    public void Provider_RefusesPathsOutsideTheBase(string path)
    {
        using var connection = new WebDavConnection { BaseUri = new Uri("https://h/dav/") };
        Assert.Throws<ArgumentException>(() => _ = new WebDavFileProvider(connection, path));
    }

    [Test]
    public void BaseUri_MustBeHttp() =>
        Assert.Throws<ArgumentException>(() => _ = new WebDavConnection { BaseUri = new Uri("ftp://h/") });

    [Test]
    public async Task Listing_SkipsSelfBehindProxy_DecodesNames_SendsBasicAuth()
    {
        // The proxy serves /dav/ but the backend writes hrefs under /internal/: names still land in the right folder.
        var handler = new CannedHandler(request =>
        {
            Assert.That(request.Method.Method, Is.EqualTo("PROPFIND"));
            Assert.That(request.Headers.GetValues("Depth").Single(), Is.EqualTo("1"));
            Assert.That(request.RequestUri!.AbsoluteUri, Is.EqualTo("https://h/dav/%D0%9F%D0%B0%D0%BF%D0%BA%D0%B0%20%231/"));
            Assert.That(request.Headers.Authorization?.Scheme, Is.EqualTo("Basic"));
            Assert.That(Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization!.Parameter!)),
                Is.EqualTo("алиса:p:w"));

            return MultiStatus("""
                <D:multistatus xmlns:D="DAV:">
                  <D:response><D:href>/internal/%D0%9F%D0%B0%D0%BF%D0%BA%D0%B0%20%231/</D:href>
                    <D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>
                  <D:response><D:href>/internal/%D0%9F%D0%B0%D0%BF%D0%BA%D0%B0%20%231/z%25.txt</D:href>
                    <D:propstat><D:prop><D:resourcetype/><D:getcontentlength>3</D:getcontentlength></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>
                  <D:response><D:href>/internal/%D0%9F%D0%B0%D0%BF%D0%BA%D0%B0%20%231/.hidden/</D:href>
                    <D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>
                  <D:response><D:href>/internal/%D0%9F%D0%B0%D0%BF%D0%BA%D0%B0%20%231/B/</D:href>
                    <D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>
                </D:multistatus>
                """);
        });

        using var connection = new WebDavConnection
        {
            BaseUri = new Uri("https://h/dav/"),
            UserName = "алиса",
            Password = "p:w",
            Handler = handler
        };

        var items = await new WebDavFileProvider(connection, "Папка #1").GetItemsAsync(FileProviderFilter.Default).ToListAsync();

        Assert.That(items.Select(i => i.Name), Is.EqualTo(new[] { "B", "z%.txt" }), "folders first, hidden skipped");
        Assert.That(items[0], Is.InstanceOf<IFolderItem>());
        Assert.That(items[1].FullName, Is.EqualTo("webdavs://h/dav/Папка #1/z%.txt"));
        Assert.That(items[1].Size, Is.EqualTo(3));
    }

    [Test]
    public void DigestOnlyServer_IsReportedAsUnsupported()
    {
        var handler = new CannedHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            response.Headers.WwwAuthenticate.ParseAdd("Digest realm=\"x\", nonce=\"y\", qop=\"auth\"");
            return response;
        });
        using var connection = new WebDavConnection { BaseUri = new Uri("https://h/"), UserName = "u", Password = "p", Handler = handler };

        Assert.ThrowsAsync<NotSupportedException>(() => connection.GetDriveAsync());
    }

    [Test]
    public void WrongPassword_IsWebDavException401()
    {
        var handler = new CannedHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            response.Headers.WwwAuthenticate.ParseAdd("Basic realm=\"x\"");
            return response;
        });
        using var connection = new WebDavConnection { BaseUri = new Uri("https://h/"), UserName = "u", Password = "p", Handler = handler };

        var ex = Assert.ThrowsAsync<WebDavException>(() => connection.GetDriveAsync());
        Assert.That(ex!.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Drive_ReadsQuota_NegativeAvailableMeansUnknown()
    {
        var quota = "<D:quota-used-bytes>100</D:quota-used-bytes><D:quota-available-bytes>900</D:quota-available-bytes>";
        var handler = new CannedHandler(request =>
        {
            Assert.That(request.Headers.GetValues("Depth").Single(), Is.EqualTo("0"));
            return MultiStatus($"""
                <D:multistatus xmlns:D="DAV:"><D:response><D:href>/dav/</D:href><D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype>{quota}</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response></D:multistatus>
                """);
        });
        using var connection = new WebDavConnection { BaseUri = new Uri("https://h/dav"), DisplayName = "My cloud", Handler = handler };

        var drive = await connection.GetDriveAsync();
        Assert.Multiple(() =>
        {
            Assert.That(drive.Name, Is.EqualTo("My cloud"));
            Assert.That(drive.FullName, Is.EqualTo("webdavs://h/dav"));
            Assert.That(drive.TotalSize, Is.EqualTo(1000));
            Assert.That(drive.TotalFreeSpace, Is.EqualTo(900));
            Assert.That(drive.UsedBytes, Is.EqualTo(100));
        });

        quota = "<D:quota-used-bytes>100</D:quota-used-bytes><D:quota-available-bytes>-3</D:quota-available-bytes>";
        drive = await connection.GetDriveAsync();
        Assert.That(drive.TotalSize, Is.Zero);
        Assert.That(drive.TotalFreeSpace, Is.Zero);
    }

    [Test]
    public async Task DeleteToRecycleBin_FailsWithoutAnyRequest()
    {
        var handler = new CannedHandler(_ => throw new AssertionException("no request may be sent"));
        using var connection = new WebDavConnection { BaseUri = new Uri("https://h/"), Handler = handler };
        var provider = new WebDavFileProvider(connection);
        var item = await provider.ResolveDestinationFileAsync(new FakeFile("a.txt"), overwrite: true);

        var result = await provider.DeleteAsync([item], toRecycleBin: true);

        Assert.That(result.Exception, Is.InstanceOf<NotSupportedException>());
    }

    [Test]
    public async Task Delete_RefusesAForeignBarePath()
    {
        var handler = new CannedHandler(_ => throw new AssertionException("no request may be sent"));
        using var connection = new WebDavConnection { BaseUri = new Uri("https://h/"), Handler = handler };

        var result = await new WebDavFileProvider(connection).DeleteAsync([new FakeFile("/home/user/a.txt")], toRecycleBin: false);

        Assert.That(result.Exception, Is.InstanceOf<ArgumentException>());
    }

    private static HttpResponseMessage MultiStatus(string xml) => new(HttpStatusCode.MultiStatus)
    {
        Content = new StringContent(xml, Encoding.UTF8, "application/xml")
    };

    private sealed class CannedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private sealed class FakeFile(string fullName) : IFileItem
    {
        public string Name => Path.GetFileName(fullName);
        public string FullName => fullName;
        public bool IsHidden => false;
        public bool IsSystem => false;
        public DateTimeOffset ChangedDate => DateTimeOffset.Now;
        long? IFileProviderItem.Size => 0;
        public long Size => 0;
        public string Extension => Path.GetExtension(fullName);
        public string NameWithoutExtension => Path.GetFileNameWithoutExtension(fullName);

        public Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default) =>
            Task.FromResult<(Stream, long)>((new MemoryStream(), 0));

        public Task DeleteAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
