namespace FileVault.Dropbox;

/// <summary>How to reach Dropbox and how hard to try.</summary>
public sealed class DropboxClientOptions
{
    /// <summary>The largest request body Dropbox takes, for <c>files/upload</c> and each upload session call.</summary>
    public const long MaxRequestBytes = 150L * 1024 * 1024;

    private readonly long _simpleUploadLimit = MaxRequestBytes;
    private readonly int _uploadChunkSize = 8 * 1024 * 1024;

    public Uri ApiBaseUri { get; init; } = new("https://api.dropboxapi.com/2/");
    public Uri ContentBaseUri { get; init; } = new("https://content.dropboxapi.com/2/");

    /// <summary>
    /// Files up to this size go up in one <c>files/upload</c> request; larger ones through an upload session.
    /// At most 150 MB.
    /// </summary>
    public long SimpleUploadLimit
    {
        get => _simpleUploadLimit;
        init => _simpleUploadLimit = value is >= 0 and <= MaxRequestBytes
            ? value
            : throw new ArgumentOutOfRangeException(nameof(SimpleUploadLimit), "0 … 150 MB");
    }

    /// <summary>
    /// The size of each upload session request. Each chunk is held in memory so that a rate-limited request can be
    /// sent again. Dropbox asks for multiples of 4 MB in concurrent sessions; the default 8 MB is one.
    /// </summary>
    public int UploadChunkSize
    {
        get => _uploadChunkSize;
        init => _uploadChunkSize = value is > 0 and <= (int)MaxRequestBytes
            ? value
            : throw new ArgumentOutOfRangeException(nameof(UploadChunkSize), "1 byte … 150 MB");
    }

    /// <summary>How many times a request answered 429/503 is sent again after its <c>Retry-After</c>.</summary>
    public int MaxRateLimitRetries { get; init; } = 3;

    /// <summary>A longer <c>Retry-After</c> is not waited out: the call fails with <see cref="DropboxApiException.RetryAfter"/>.</summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The wait when a 429/503 names none.</summary>
    public TimeSpan DefaultRetryAfter { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The pause between polls of a batch copy or move job.</summary>
    public TimeSpan JobPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long a batch copy or move job is polled before the call fails.</summary>
    public TimeSpan JobTimeout { get; init; } = TimeSpan.FromMinutes(30);
}

public enum DropboxEntryKind
{
    File,
    Folder
}

/// <summary>
/// A file or folder as Dropbox lists it. <see cref="Path"/> is <c>path_display</c> (the casing to show; Dropbox
/// compares paths case-insensitively), <see cref="PathLower"/> the lowercased form. Folders have no size and no
/// dates.
/// </summary>
public sealed record DropboxEntry(
    DropboxEntryKind Kind,
    string Name,
    string Path,
    string PathLower,
    string? Id,
    long Size,
    DateTimeOffset? ClientModified,
    DateTimeOffset? ServerModified,
    string? Rev,
    string? ContentHash)
{
    internal static DropboxEntry? From(ApiMetadata metadata, string fallbackPath = "")
    {
        var kind = metadata.Tag switch
        {
            null or "file" => DropboxEntryKind.File,
            "folder" => DropboxEntryKind.Folder,
            _ => (DropboxEntryKind?)null
        };

        if (kind is null)
            return null;

        var path = metadata.PathDisplay ?? metadata.PathLower ?? fallbackPath;

        return new DropboxEntry(kind.Value, metadata.Name, path, metadata.PathLower ?? path.ToLowerInvariant(),
            metadata.Id, metadata.Size ?? 0, metadata.ClientModified, metadata.ServerModified, metadata.Rev,
            metadata.ContentHash);
    }
}

/// <summary>
/// The account's space. <see cref="Allocated"/> is the quota that applies to this user: the personal one, a team
/// member's own limit, or the whole team's space when the member has none.
/// </summary>
public sealed record DropboxSpaceUsage(long Used, long Allocated, bool IsTeam)
{
    public long Free => Math.Max(0, Allocated - Used);
}
