namespace FileVault.WebDav.Tests;

public class WebDavPathTests
{
    [TestCase("/Папка #1/a b%.txt", "/%D0%9F%D0%B0%D0%BF%D0%BA%D0%B0%20%231/a%20b%25.txt")]
    [TestCase("/q?x=1&y/плюс+", "/q%3Fx%3D1%26y/%D0%BF%D0%BB%D1%8E%D1%81%2B")]
    [TestCase("", "")]
    public void Encode_EscapesEverySegment(string path, string expected) =>
        Assert.That(WebDavPath.Encode(path), Is.EqualTo(expected));

    [TestCase("https://host:8443/dav/a%20b/", "/dav/a b")]
    [TestCase("/dav/%D0%9F%D0%B0%D0%BF%D0%BA%D0%B0%20%231/", "/dav/Папка #1")]
    [TestCase("/dav/raw #hash?.txt", "/dav/raw #hash?.txt")]
    [TestCase("http://h", "")]
    [TestCase("/", "")]
    [TestCase("child%20file", "/base/child file")]
    public void DecodeHref_AnyHrefForm(string href, string expected) =>
        Assert.That(WebDavPath.DecodeHref(href, new Uri("http://h/base/")), Is.EqualTo(expected));

    [Test]
    public void Encode_Then_DecodeHref_RoundTrips()
    {
        const string path = "/dav/Отчёт #1 100% (копия)/a+b ~c'd.txt";
        Assert.That(WebDavPath.DecodeHref(WebDavPath.Encode(path), new Uri("http://h/")), Is.EqualTo(path));
    }

    [TestCase("/a//b/", "/a/b")]
    [TestCase("/", "")]
    [TestCase("a", "/a")]
    public void Normalize_Canonical(string path, string expected) =>
        Assert.That(WebDavPath.Normalize(path), Is.EqualTo(expected));

    [TestCase("/a/../b")]
    [TestCase("./a")]
    public void Normalize_RefusesDotSegments(string path) =>
        Assert.Throws<ArgumentException>(() => WebDavPath.Normalize(path));

    [TestCase("a/b")]
    [TestCase("..")]
    [TestCase("")]
    public void EnsureValidName_RefusesBadNames(string name) =>
        Assert.Throws<ArgumentException>(() => WebDavPath.EnsureValidName(name));

    [Test]
    public void ParentAndName()
    {
        Assert.That(WebDavPath.Parent("/a/b"), Is.EqualTo("/a"));
        Assert.That(WebDavPath.Parent("/a"), Is.EqualTo(""));
        Assert.That(WebDavPath.GetName("/a/b c.txt"), Is.EqualTo("b c.txt"));
    }
}
