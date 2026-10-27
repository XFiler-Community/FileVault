namespace FileVault.OneDrive;

/// <summary>
/// Returns a Microsoft Graph access token for the signed-in account. <paramref name="forceRefresh"/> is true after Graph
/// refused the previous token (401): the source must get a new one instead of returning a cached token.
/// <see cref="OneDriveTokenSource.GetAccessTokenAsync"/> fits this delegate.
/// </summary>
public delegate ValueTask<string> OneDriveAccessTokenProvider(bool forceRefresh, CancellationToken ct);

public sealed class OneDriveClientOptions
{
    /// <summary>One fragment of an upload session must be a multiple of 320 KiB.</summary>
    public const int UploadFragmentUnit = 320 * 1024;

    private const int MaxUploadChunkSize = 60 * 1024 * 1024;

    public Uri GraphBaseAddress { get; init; } = new("https://graph.microsoft.com/v1.0/");

    /// <summary>
    /// How many times a request answered 429 or 503 (and 500/502/504 for upload fragments and copy monitors) is sent
    /// again. The wait is the answer's <c>Retry-After</c>, else 1, 2, 4… s, never longer than <see cref="MaxRetryDelay"/>.
    /// </summary>
    public int MaxRetries { get; init; } = 4;

    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Files up to this size are uploaded with one <c>PUT …/content</c>; larger ones through an upload session.</summary>
    public long SimpleUploadLimit { get; init; } = 4 * 1024 * 1024;

    /// <summary>Bytes per upload-session fragment: a multiple of 320 KiB, at most 60 MiB. Default 10 MiB as Graph advises.</summary>
    public int UploadChunkSize { get; init; } = 32 * UploadFragmentUnit;

    /// <summary>Items per listing page (<c>$top</c>).</summary>
    public int PageSize { get; init; } = 200;

    /// <summary>First wait between polls of a server-side copy; it doubles up to 5 s.</summary>
    public TimeSpan CopyPollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    internal void Validate()
    {
        if (UploadChunkSize <= 0 || UploadChunkSize % UploadFragmentUnit != 0 || UploadChunkSize > MaxUploadChunkSize)
            throw new ArgumentException(
                $"{nameof(UploadChunkSize)} must be a positive multiple of 320 KiB (327 680 bytes), at most 60 MiB.");

        if (PageSize <= 0)
            throw new ArgumentException($"{nameof(PageSize)} must be positive.");

        if (MaxRetries < 0)
            throw new ArgumentException($"{nameof(MaxRetries)} must not be negative.");
    }
}
