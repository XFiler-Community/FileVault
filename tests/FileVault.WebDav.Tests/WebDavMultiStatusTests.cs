using System.Net;

namespace FileVault.WebDav.Tests;

/// <summary>PROPFIND answers as real servers write them: prefixes, href forms and property sets differ.</summary>
public class WebDavMultiStatusTests
{
    private static readonly Uri NextcloudRequest = new("https://cloud.example.com/remote.php/dav/files/alice/Docs/");

    [Test]
    public void Nextcloud_LowercasePrefix_EncodedHrefs_QuotaAndMissingProps()
    {
        const string xml = """
            <?xml version="1.0"?>
            <d:multistatus xmlns:d="DAV:" xmlns:s="http://sabredav.org/ns" xmlns:oc="http://owncloud.org/ns" xmlns:nc="http://nextcloud.org/ns">
              <d:response>
                <d:href>/remote.php/dav/files/alice/Docs/</d:href>
                <d:propstat>
                  <d:prop>
                    <d:resourcetype><d:collection/></d:resourcetype>
                    <d:getlastmodified>Tue, 27 Oct 2026 15:17:18 GMT</d:getlastmodified>
                    <d:quota-used-bytes>1048576</d:quota-used-bytes>
                    <d:quota-available-bytes>-3</d:quota-available-bytes>
                  </d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status>
                </d:propstat>
                <d:propstat>
                  <d:prop><d:getcontentlength/></d:prop>
                  <d:status>HTTP/1.1 404 Not Found</d:status>
                </d:propstat>
              </d:response>
              <d:response>
                <d:href>/remote.php/dav/files/alice/Docs/%d0%9e%d1%82%d1%87%d1%91%d1%82%20%231%20100%25.pdf</d:href>
                <d:propstat>
                  <d:prop>
                    <d:resourcetype/>
                    <d:getcontentlength>2048</d:getcontentlength>
                    <d:getlastmodified>Mon, 05 Jan 2026 08:00:00 GMT</d:getlastmodified>
                    <oc:size>2048</oc:size>
                  </d:prop>
                  <d:status>HTTP/1.1 200 OK</d:status>
                </d:propstat>
              </d:response>
            </d:multistatus>
            """;

        var resources = WebDavMultiStatus.Parse(xml, NextcloudRequest);

        Assert.That(resources, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(resources[0].Path, Is.EqualTo("/remote.php/dav/files/alice/Docs"));
            Assert.That(resources[0].IsCollection, Is.True);
            Assert.That(resources[0].Length, Is.Null);
            Assert.That(resources[0].QuotaUsedBytes, Is.EqualTo(1048576));
            Assert.That(resources[0].QuotaAvailableBytes, Is.EqualTo(-3));
            Assert.That(resources[0].LastModified, Is.EqualTo(new DateTimeOffset(2026, 10, 27, 15, 17, 18, TimeSpan.Zero)));

            Assert.That(resources[1].Path, Is.EqualTo("/remote.php/dav/files/alice/Docs/Отчёт #1 100%.pdf"));
            Assert.That(resources[1].IsCollection, Is.False);
            Assert.That(resources[1].Length, Is.EqualTo(2048));
        });
    }

    [Test]
    public void Iis_UppercasePrefix_AbsoluteUrlHrefs_TypedAttributes()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <D:multistatus xmlns:D="DAV:" xmlns:b="urn:uuid:c2f41010-65b3-11d1-a29f-00aa00c14882/">
              <D:response>
                <D:href>http://server/share/</D:href>
                <D:propstat>
                  <D:status>HTTP/1.1 200 OK</D:status>
                  <D:prop>
                    <D:resourcetype><D:collection/></D:resourcetype>
                    <D:creationdate b:dt="dateTime.tz">2025-03-01T10:00:00.000Z</D:creationdate>
                  </D:prop>
                </D:propstat>
              </D:response>
              <D:response>
                <D:href>http://server/share/My%20Folder/</D:href>
                <D:propstat>
                  <D:status>HTTP/1.1 200 OK</D:status>
                  <D:prop>
                    <D:resourcetype><D:collection/></D:resourcetype>
                    <D:getlastmodified b:dt="dateTime.rfc1123">Sat, 01 Mar 2025 10:00:00 GMT</D:getlastmodified>
                  </D:prop>
                </D:propstat>
              </D:response>
              <D:response>
                <D:href>http://server/share/a%2bb.txt</D:href>
                <D:propstat>
                  <D:status>HTTP/1.1 200 OK</D:status>
                  <D:prop>
                    <D:resourcetype/>
                    <D:getcontentlength b:dt="int">17</D:getcontentlength>
                  </D:prop>
                </D:propstat>
              </D:response>
            </D:multistatus>
            """;

        var resources = WebDavMultiStatus.Parse(xml, new Uri("http://server/share/"));

        Assert.That(resources.Select(r => r.Path), Is.EqualTo(new[] { "/share", "/share/My Folder", "/share/a+b.txt" }));
        Assert.Multiple(() =>
        {
            Assert.That(resources[0].LastModified, Is.EqualTo(new DateTimeOffset(2025, 3, 1, 10, 0, 0, TimeSpan.Zero)),
                "creationdate is the fallback for a missing getlastmodified");
            Assert.That(resources[1].IsCollection, Is.True);
            Assert.That(resources[2].Length, Is.EqualTo(17));
        });
    }

    [Test]
    public void Yandex_QuotaOnRoot_UnencodedCyrillicHref()
    {
        const string xml = """
            <?xml version='1.0' encoding='UTF-8'?>
            <d:multistatus xmlns:d="DAV:">
              <d:response>
                <d:href>/</d:href>
                <d:propstat>
                  <d:status>HTTP/1.1 200 OK</d:status>
                  <d:prop>
                    <d:quota-used-bytes>5368709120</d:quota-used-bytes>
                    <d:quota-available-bytes>5368709120</d:quota-available-bytes>
                    <d:resourcetype><d:collection/></d:resourcetype>
                  </d:prop>
                </d:propstat>
              </d:response>
              <d:response>
                <d:href>/Фото/</d:href>
                <d:propstat>
                  <d:status>HTTP/1.1 200 OK</d:status>
                  <d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop>
                </d:propstat>
              </d:response>
            </d:multistatus>
            """;

        var resources = WebDavMultiStatus.Parse(xml, new Uri("https://webdav.yandex.ru/"));

        Assert.That(resources.Select(r => r.Path), Is.EqualTo(new[] { "", "/Фото" }));
        Assert.That(resources[0].QuotaUsedBytes, Is.EqualTo(5368709120));
        Assert.That(resources[0].QuotaAvailableBytes, Is.EqualTo(5368709120));
    }

    [Test]
    public void DefaultNamespace_And_NoNamespace_AreRead()
    {
        const string defaultNs = """
            <multistatus xmlns="DAV:"><response><href>/a/b%20c</href><propstat><prop><resourcetype/><getcontentlength>5</getcontentlength></prop><status>HTTP/1.1 200 OK</status></propstat></response></multistatus>
            """;
        const string noNs = """
            <multistatus><response><href>/a/d/</href><propstat><prop><resourcetype><collection/></resourcetype></prop></propstat></response></multistatus>
            """;

        var first = WebDavMultiStatus.Parse(defaultNs, new Uri("http://h/a/")).Single();
        var second = WebDavMultiStatus.Parse(noNs, new Uri("http://h/a/")).Single();

        Assert.That(first.Path, Is.EqualTo("/a/b c"));
        Assert.That(first.Length, Is.EqualTo(5));
        Assert.That(second.Path, Is.EqualTo("/a/d"));
        Assert.That(second.IsCollection, Is.True);
    }

    [Test]
    public void ForeignNamespaceProperties_DoNotCountAsDavOnes()
    {
        const string xml = """
            <d:multistatus xmlns:d="DAV:" xmlns:x="urn:other"><d:response><d:href>/f</d:href><d:propstat><d:prop><x:getcontentlength>99</x:getcontentlength><d:getcontentlength>1</d:getcontentlength></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>
            """;

        Assert.That(WebDavMultiStatus.Parse(xml, new Uri("http://h/")).Single().Length, Is.EqualTo(1));
    }

    [Test]
    public void ResponseWithErrorStatus_IsSkipped()
    {
        const string xml = """
            <d:multistatus xmlns:d="DAV:">
              <d:response><d:href>/gone.txt</d:href><d:status>HTTP/1.1 404 Not Found</d:status></d:response>
              <d:response><d:href>/here.txt</d:href><d:propstat><d:prop><d:resourcetype/></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
            </d:multistatus>
            """;

        Assert.That(WebDavMultiStatus.Parse(xml, new Uri("http://h/")).Select(r => r.Path), Is.EqualTo(new[] { "/here.txt" }));
    }

    [Test]
    public void NotAMultistatus_Throws()
    {
        var ex = Assert.Throws<WebDavException>(() => WebDavMultiStatus.Parse("<html><body>Login</body></html>", new Uri("http://h/")));
        Assert.That(ex!.StatusCode, Is.EqualTo(HttpStatusCode.MultiStatus));
    }

    [Test]
    public void Dtd_IsRefused()
    {
        const string xml = """
            <?xml version="1.0"?><!DOCTYPE d [<!ENTITY x SYSTEM "file:///etc/passwd">]><d:multistatus xmlns:d="DAV:"><d:response><d:href>&x;</d:href></d:response></d:multistatus>
            """;

        Assert.Throws<System.Xml.XmlException>(() => WebDavMultiStatus.Parse(xml, new Uri("http://h/")));
    }
}
