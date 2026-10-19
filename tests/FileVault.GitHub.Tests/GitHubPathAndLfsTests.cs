using System.Text;

namespace FileVault.GitHub.Tests;

[TestFixture]
public class GitHubPathAndLfsTests
{
    [TestCase("github:anna/art-life@main:/", "anna", "art-life", "main", "")]
    [TestCase("github:anna/art-life@main:/applications/a.md", "anna", "art-life", "main", "applications/a.md")]
    [TestCase("github:anna/art-life@claude/open-calls:/a.md", "anna", "art-life", "claude/open-calls", "a.md")]
    public void Parse_RoundTrips(string fullName, string owner, string name, string branch, string path)
    {
        Assert.That(GitHubPath.TryParse(fullName, out var o, out var n, out var b, out var p), Is.True);
        Assert.That((o, n, b, p), Is.EqualTo((owner, name, branch, path)));
        Assert.That(GitHubPath.Format(o, n, b, p), Is.EqualTo(fullName.TrimEnd('/') + (p.Length == 0 ? "/" : "")));
    }

    [TestCase("gdrive:root")]
    [TestCase("github:anna@main:/a")]
    [TestCase("github:anna/art-life:/a")]
    [TestCase("github:anna/art-life@main:/../secrets")]
    [TestCase("github:anna/art-life@main:/.git/config")]
    public void Parse_RefusesWhatIsNotASafeGitHubPath(string fullName) =>
        Assert.That(GitHubPath.TryParse(fullName, out _, out _, out _, out _), Is.False);

    [TestCase("*.psd", "art/cover.psd", true)]
    [TestCase("*.psd", "cover.psd.md", false)]
    [TestCase("portfolio/**", "portfolio/2026/a.png", true)]
    [TestCase("portfolio/**", "other/portfolio/a.png", false)]
    [TestCase("/scans/*.pdf", "scans/a.pdf", true)]
    [TestCase("/scans/*.pdf", "scans/old/a.pdf", false)]
    [TestCase("**/raw/*.cr[23]", "shoots/raw/img.cr3", true)]
    public void GitAttributes_MatchLikeGit(string pattern, string path, bool isLfs) =>
        Assert.That(GitAttributes.Parse($"{pattern} filter=lfs diff=lfs merge=lfs -text").IsLfs(path), Is.EqualTo(isLfs));

    [Test]
    public void GitAttributes_LaterLineWins_CommentsIgnored()
    {
        var attributes = GitAttributes.Parse("# images\n*.png filter=lfs\nicons/*.png -filter\n*.md text\n");

        Assert.That(attributes.IsLfs("art/a.png"), Is.True);
        Assert.That(attributes.IsLfs("icons/a.png"), Is.False);
        Assert.That(attributes.IsLfs("notes.md"), Is.False);
    }

    [Test]
    public void LfsPointer_ParsedOnlyFromRealPointers()
    {
        var oid = new string('a', 64);
        var pointer = Encoding.UTF8.GetBytes($"version https://git-lfs.github.com/spec/v1\noid sha256:{oid}\nsize 12345\n");

        Assert.That(GitLfsPointer.TryParse(pointer), Is.EqualTo(new GitLfsPointer(oid, 12345)));
        Assert.That(GitLfsPointer.TryParse("# just markdown\n"u8), Is.Null);
        Assert.That(GitLfsPointer.TryParse(new byte[] { 0xff, 0xfe, 0x00 }), Is.Null);
    }

    [Test]
    public void BlobSha_MatchesGit()
    {
        // printf 'hello\n' | git hash-object --stdin
        Assert.That(GitHubRepository.BlobSha("hello\n"u8), Is.EqualTo("ce013625030ba8dba906f756967f9e9ca394464a"));
    }
}
