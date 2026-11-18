using System.Collections.Concurrent;

namespace FileVault.Core;

/// <summary>
/// Copies a file between two clouds without passing it through this computer: the destination downloads the source's
/// temporary link (<see cref="ITemporaryLinkItem"/>) by itself. A provider that can fetch a URL calls
/// <see cref="TryAsync"/> from its <c>CopyFileInAsync</c> before streaming; whenever this does not work out — no link,
/// a file too small to be worth it, the destination refused or failed, the result has another size — the provider
/// streams the file as before, so the copy itself never fails because of it.
/// </summary>
/// <remarks>
/// The destination reports no progress while it downloads. It may also save whatever the link answers (an error
/// page, say), so the size it ends up with is compared with the source's and a wrong file is removed. A destination
/// cannot be told to stop downloading, so a cancelled copy ends only once it has finished and its file is removed:
/// <see cref="TryAsync"/> throws then, not at the moment of the cancel. If the process exits before that, the file
/// stays at the destination.
/// </remarks>
public static class UrlTransfer
{
    private static readonly ConcurrentDictionary<string, Task> Cancelling = new(StringComparer.Ordinal);

    /// <summary>How long a cancelled copy waits for the destination to finish, so its file can be removed. An hour by default.</summary>
    public static TimeSpan CancelledCopyWait { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Raised when a cancelled copy could not be taken back, so its file may be at the destination: the source file's
    /// name and the error if there was one. For logs; it changes nothing.
    /// </summary>
    public static event Action<string, Exception?>? CancelledCopyLeft;

    /// <summary>False turns URL transfers off for the process: every copy streams. True by default.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// Smaller files are streamed — about as fast, and with progress. 16 MiB by default, where downloads start going
    /// in ranges (<see cref="ParallelDownloadOptions.MinimumLength"/>).
    /// </summary>
    public static long MinimumSize { get; set; } = 16 * 1024 * 1024;

    /// <summary>
    /// Raised when a URL transfer was tried and the copy falls back to streaming: the source file's name, why, and the
    /// error if there was one. For logs; it changes nothing.
    /// </summary>
    public static event Action<string, string, Exception?>? FellBack;

    /// <summary>
    /// Lets the destination download <paramref name="source"/> by its temporary link.
    /// </summary>
    /// <param name="fetch">
    /// Asks the destination to download the link into its path, waits until it is done and returns the size of the
    /// file it ended up with. Throws when the destination refused (the path exists, the link is unreachable) or failed.
    /// </param>
    /// <param name="discard">
    /// Removes the file <paramref name="fetch"/> left at the destination path; called when that file has another size
    /// than the source (a failure is ignored: streaming then replaces the file) and when the copy was cancelled.
    /// </param>
    /// <param name="ct">
    /// Cancels the copy. <paramref name="fetch"/> does not get it: it goes on until the destination is done, so that
    /// what the destination downloaded can be removed.
    /// </param>
    /// <param name="destination">
    /// The destination path: a copy to it waits until a cancelled copy to the same path is taken back, or that one
    /// would remove the new file.
    /// </param>
    /// <returns>
    /// True when the destination holds a file of the source's size. False when the caller should stream the file
    /// instead; the destination then holds nothing new.
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static async Task<bool> TryAsync(IFileItem source,
        Func<Uri, CancellationToken, Task<long>> fetch,
        Func<CancellationToken, Task> discard,
        CancellationToken ct = default,
        string? destination = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(fetch);
        ArgumentNullException.ThrowIfNull(discard);

        if (destination is not null)
            await WhenCancelledCopyGoneAsync(destination, ct).ConfigureAwait(false);

        if (!Enabled || source is not ITemporaryLinkItem linked || source.Size < Math.Max(1, MinimumSize))
            return false;

        Uri? link;

        try
        {
            link = await linked.GetTemporaryLinkAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Report(source, "the source gave no temporary link", ex);
            return false;
        }

        // a plain http link would hand the file and its signature to anyone on the way
        if (link is null || link.Scheme != Uri.UriSchemeHttps)
            return false;

        long size;
        Task<long>? fetching = null;
        using var limit = new CancellationTokenSource();

        try
        {
            fetching = fetch(link, limit.Token);
            size = await fetching.WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && fetching is not null)
        {
            limit.CancelAfter(CancelledCopyWait);
            await TakeBackAsync(source, destination, fetching, discard, limit.Token).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Report(source, "the destination did not download the link", ex);
            return false;
        }

        if (size == source.Size)
            return true;

        Report(source, $"the destination got {size} bytes instead of {source.Size}", null);

        try
        {
            await discard(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // streaming overwrites it
        }

        return false;
    }

    /// <summary>Waits until a cancelled copy to <paramref name="destination"/>, if there is one, is taken back.</summary>
    public static Task WhenCancelledCopyGoneAsync(string destination, CancellationToken ct = default) =>
        Cancelling.TryGetValue(destination, out var cancelling) ? cancelling.WaitAsync(ct) : Task.CompletedTask;

    private static async Task TakeBackAsync(IFileItem source, string? destination, Task<long> fetching,
        Func<CancellationToken, Task> discard, CancellationToken limit)
    {
        var takingBack = RunAsync();

        if (destination is not null)
            Cancelling[destination] = takingBack;

        try
        {
            await takingBack.ConfigureAwait(false);
        }
        finally
        {
            if (destination is not null)
                Cancelling.TryRemove(new KeyValuePair<string, Task>(destination, takingBack));
        }

        async Task RunAsync()
        {
            try
            {
                await fetching.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // a destination that failed left nothing; one still busy after the wait may finish later
                if (limit.IsCancellationRequested)
                    ReportLeft(source, ex);

                return;
            }

            try
            {
                await discard(limit).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ReportLeft(source, ex);
            }
        }
    }

    private static void ReportLeft(IFileItem source, Exception? exception)
    {
        try
        {
            CancelledCopyLeft?.Invoke(source.Name, exception);
        }
        catch
        {
            // a log must not break the copy
        }
    }

    private static void Report(IFileItem source, string reason, Exception? exception)
    {
        try
        {
            FellBack?.Invoke(source.Name, reason, exception);
        }
        catch
        {
            // a log must not break the copy
        }
    }
}
