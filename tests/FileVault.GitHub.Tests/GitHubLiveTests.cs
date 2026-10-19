using FileVault.Core;

namespace FileVault.GitHub.Tests;

/// <summary>
/// Reads a public repository on the real GitHub, without a token (60 requests an hour), to check that the fake
/// speaks the same API. Opt in with GITHUB_LIVE_TESTS=1.
/// </summary>
[TestFixture]
[Category("Integration")]
public class GitHubLiveTests
{
    private GitHubRepository _repository = null!;

    [OneTimeSetUp]
    public async Task Connect()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_LIVE_TESTS") != "1")
            Assert.Ignore("Set GITHUB_LIVE_TESTS=1 to read a public repository on GitHub.");

        var options = new GitHubRepositoryOptions { Owner = "XFiler-Community", Name = "XFiler", Branch = "main" };
        var probe = new GitHubRepository(options, new HttpClient());
        var branch = (await probe.GetInfoAsync()).DefaultBranch;

        _repository = new GitHubRepository(new GitHubRepositoryOptions
        {
            Owner = options.Owner,
            Name = options.Name,
            Branch = branch
        }, new HttpClient());
    }

    [Test]
    public async Task ListsReadsAndShowsHistory()
    {
        var items = await _repository.CreateProvider().GetItemsAsync(FileProviderFilter.ShowAll).ToListAsync();
        var file = items.OfType<IFileItem>().First(f => f.Size is > 0 and < 200_000);
        var (stream, length) = await file.OpenReadAsync();
        await using (stream) { }

        var commits = await _repository.GetCommitsAsync(perPage: 3);
        var details = await _repository.GetCommitAsync(commits[0].Sha);
        var comparison = await _repository.CompareAsync(commits[^1].Sha, _repository.Branch);

        TestContext.Out.WriteLine(string.Join(", ", items.Select(i => i.Name)));
        Assert.That(length, Is.EqualTo(file.Size));
        Assert.That(details!.Files, Is.Not.Empty);
        Assert.That(comparison.AheadBy, Is.EqualTo(commits.Count - 1));
    }
}
