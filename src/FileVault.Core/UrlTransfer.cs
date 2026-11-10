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
/// page, say), so the size it ends up with is compared with the source's and a wrong file is removed. A cancelled
/// copy stops waiting, but the destination may still finish its download and the file appear later.
/// </remarks>
public static class UrlTransfer
{
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
    /// Removes the file <paramref name="fetch"/> left at the destination path; called only when that file has another
    /// size than the source. A failure here is ignored: streaming then replaces the file.
    /// </param>
    /// <returns>
    /// True when the destination holds a file of the source's size. False when the caller should stream the file
    /// instead; the destination then holds nothing new.
    /// </returns>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static async Task<bool> TryAsync(IFileItem source,
        Func<Uri, CancellationToken, Task<long>> fetch,
        Func<CancellationToken, Task> discard,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(fetch);
        ArgumentNullException.ThrowIfNull(discard);

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

        try
        {
            size = await fetch(link, ct).ConfigureAwait(false);
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
