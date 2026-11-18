namespace FileVault.Core.Tests;

[TestFixture]
public class UrlTransferTests
{
    private const long Large = 64L * 1024 * 1024;
    private static readonly Uri Link = new("https://dl.example.com/file?sig=abc");

    [Test]
    public async Task SameSize_IsDone()
    {
        Uri? asked = null;

        var done = await UrlTransfer.TryAsync(new Linked(Large, Link),
            (link, _) => { asked = link; return Task.FromResult(Large); },
            _ => throw new AssertionException("nothing to discard"));

        Assert.That(done, Is.True);
        Assert.That(asked, Is.EqualTo(Link));
    }

    [Test]
    public async Task OtherSize_IsDiscardedAndStreamed()
    {
        var discarded = false;
        var reasons = new List<string>();
        void Log(string name, string reason, Exception? _) => reasons.Add($"{name}: {reason}");
        UrlTransfer.FellBack += Log;

        try
        {
            var done = await UrlTransfer.TryAsync(new Linked(Large, Link),
                (_, _) => Task.FromResult(512L),
                _ => { discarded = true; return Task.CompletedTask; });

            Assert.That(done, Is.False);
            Assert.That(discarded, Is.True);
            Assert.That(reasons, Has.One.Contains("512 bytes"));
        }
        finally
        {
            UrlTransfer.FellBack -= Log;
        }
    }

    [Test]
    public async Task DestinationFails_StreamsWithoutDiscarding()
    {
        var done = await UrlTransfer.TryAsync(new Linked(Large, Link),
            (_, _) => throw new HttpRequestException("409 exists"),
            _ => throw new AssertionException("an existing file must not be removed"));

        Assert.That(done, Is.False);
    }

    [Test]
    public async Task FailedDiscard_StillStreams()
    {
        var done = await UrlTransfer.TryAsync(new Linked(Large, Link),
            (_, _) => Task.FromResult(1L),
            _ => throw new IOException("gone"));

        Assert.That(done, Is.False);
    }

    [TestCase(100L, "https://dl.example.com/f", TestName = "SmallFile_IsStreamed")]
    [TestCase(Large, "http://dl.example.com/f", TestName = "PlainHttpLink_IsStreamed")]
    public async Task NotTried(long size, string link)
    {
        var done = await UrlTransfer.TryAsync(new Linked(size, new Uri(link)),
            (_, _) => throw new AssertionException("the destination must not be asked"),
            _ => Task.CompletedTask);

        Assert.That(done, Is.False);
    }

    [Test]
    public async Task NoLink_IsStreamed()
    {
        var done = await UrlTransfer.TryAsync(new Linked(Large, null),
            (_, _) => throw new AssertionException("the destination must not be asked"),
            _ => Task.CompletedTask);

        Assert.That(done, Is.False);
    }

    [Test]
    public async Task SourceWithoutLinks_IsStreamed()
    {
        var done = await UrlTransfer.TryAsync(new Plain(Large),
            (_, _) => throw new AssertionException("the destination must not be asked"),
            _ => Task.CompletedTask);

        Assert.That(done, Is.False);
    }

    [Test]
    public void Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();

        Assert.That(async () => await UrlTransfer.TryAsync(new Linked(Large, Link),
                async (_, token) => { await cts.CancelAsync(); token.ThrowIfCancellationRequested(); return 0L; },
                _ => Task.CompletedTask, cts.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task Cancelled_WaitsForTheDestination_ThenRemovesItsFile()
    {
        using var cts = new CancellationTokenSource();
        var downloaded = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetchToken = CancellationToken.None;
        var discarded = false;

        var copy = UrlTransfer.TryAsync(new Linked(Large, Link),
            (_, token) => { fetchToken = token; return downloaded.Task; },
            _ => { discarded = true; return Task.CompletedTask; }, cts.Token);

        await cts.CancelAsync();
        await Task.Delay(50);

        Assert.That(copy.IsCompleted, Is.False, "the destination is still downloading");
        Assert.That(fetchToken.IsCancellationRequested, Is.False);
        Assert.That(discarded, Is.False);

        downloaded.SetResult(Large);

        Assert.That(async () => await copy, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(discarded, Is.True);
    }

    [Test]
    public async Task Cancelled_DestinationFailed_RemovesNothing()
    {
        using var cts = new CancellationTokenSource();
        var downloaded = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);

        var copy = UrlTransfer.TryAsync(new Linked(Large, Link),
            (_, _) => downloaded.Task,
            _ => throw new AssertionException("a file that was there before must not be removed"), cts.Token);

        await cts.CancelAsync();
        downloaded.SetException(new HttpRequestException("409 exists"));

        Assert.That(async () => await copy, Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public async Task NewCopy_WaitsUntilTheCancelledOneToTheSamePathIsGone()
    {
        const string destination = "disk:/film.mp4";
        using var cts = new CancellationTokenSource();
        var downloaded = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();

        var cancelled = UrlTransfer.TryAsync(new Linked(Large, Link),
            (_, _) => downloaded.Task,
            _ => { order.Add("removed"); return Task.CompletedTask; }, cts.Token, destination);

        await cts.CancelAsync();
        await Task.Delay(50);

        var again = UrlTransfer.TryAsync(new Linked(Large, Link),
            (_, _) => { order.Add("copied"); return Task.FromResult(Large); },
            _ => Task.CompletedTask, destination: destination);

        await Task.Delay(50);
        Assert.That(order, Is.Empty);

        downloaded.SetResult(Large);

        Assert.That(async () => await cancelled, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(await again, Is.True);
        Assert.That(order, Is.EqualTo(new[] { "removed", "copied" }));
    }

    private class Plain(long size) : IFileItem
    {
        public string Name => "film.mp4";
        public string FullName => "other:film.mp4";
        public bool IsHidden => false;
        public bool IsSystem => false;
        public DateTimeOffset ChangedDate => DateTimeOffset.Now;
        long? IFileProviderItem.Size => size;
        public long Size => size;
        public string Extension => ".mp4";
        public string NameWithoutExtension => "film";

        public Task<(Stream stream, long totalBytes)> OpenReadAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Linked(long size, Uri? link) : Plain(size), ITemporaryLinkItem
    {
        public Task<Uri?> GetTemporaryLinkAsync(CancellationToken ct = default) => Task.FromResult(link);
    }
}
