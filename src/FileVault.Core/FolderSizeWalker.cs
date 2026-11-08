using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace FileVault.Core;

/// <summary>
/// <see cref="IFolderSizeProvider.MeasureFoldersAsync"/> for a backend that keeps no sizes and has no recursive listing
/// (FTP, SFTP, WebDAV): every folder below is listed through <see cref="IFolderItem.CreateProvider"/>. A subfolder
/// whose listing fails is skipped; only a failure of the provider's own listing is thrown.
/// </summary>
public static class FolderSizeWalker
{
    /// <summary>How often a snapshot is taken while the walk runs.</summary>
    public static TimeSpan ReportInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <param name="provider">The folder whose subfolders are measured.</param>
    /// <param name="maxParallelism">Listings at once; 1 for a backend whose connection serves one request at a time.</param>
    /// <param name="comparer">How the backend compares names.</param>
    public static async IAsyncEnumerable<FolderSizes> MeasureAsync(IFileProvider provider, int maxParallelism = 1,
        IEqualityComparer<string>? comparer = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxParallelism, 1);

        var tally = new FolderSizeTally(comparer);
        var work = Channel.CreateUnbounded<(string top, IFolderItem folder)>();
        var pending = 0;

        await foreach (var item in provider.GetItemsAsync(FileProviderFilter.ShowAll, ct).ConfigureAwait(false))
        {
            if (item is not IFolderItem folder)
                continue;

            tally.AddFolder(folder.Name);
            pending++;
            work.Writer.TryWrite((folder.Name, folder));
        }

        if (pending == 0)
        {
            yield return tally.Snapshot(isComplete: true);
            yield break;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        async Task WorkAsync()
        {
            await foreach (var (top, folder) in work.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                try
                {
                    await foreach (var item in folder.CreateProvider().GetItemsAsync(FileProviderFilter.ShowAll, stop.Token)
                                       .ConfigureAwait(false))
                    {
                        if (item is IFolderItem child)
                        {
                            Interlocked.Increment(ref pending);
                            work.Writer.TryWrite((top, child));
                        }
                        else
                            tally.Add(top, item.Size ?? 0);
                    }
                }
                catch (Exception) when (!stop.IsCancellationRequested)
                {
                    // a folder that cannot be listed (no access, gone meanwhile) adds nothing, as `du` skips it
                }

                if (Interlocked.Decrement(ref pending) == 0)
                    work.Writer.TryComplete();
            }
        }

        var workers = Task.WhenAll(Enumerable.Range(0, maxParallelism).Select(_ => Task.Run(WorkAsync, stop.Token)));

        try
        {
            while (!workers.IsCompleted)
            {
                await Task.WhenAny(workers, Task.Delay(ReportInterval, ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();

                if (!workers.IsCompleted && tally.HasChanged)
                    yield return tally.Snapshot(isComplete: false);
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            work.Writer.TryComplete();

            try
            {
                await workers.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        yield return tally.Snapshot(isComplete: true);
    }
}
