using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FileVault.Core;

namespace FileVault.Dropbox;

/// <summary>
/// One Dropbox account. Items and providers created from one instance count as the same provider, so copies and
/// moves between them stay on Dropbox (<c>copy_v2</c> / <c>move_v2</c>); items of another instance — even of the
/// same account — go through a download and an upload.
/// </summary>
public sealed class DropboxClient
{
    private const int ListPageLimit = 2000;
    private const int SearchPageSize = 200;

    private static DropboxJsonContext Json => DropboxJsonContext.Default;

    /// <param name="tokenSource">Hands out access tokens and refreshes them when Dropbox refuses one as expired.</param>
    /// <param name="httpClient">Used for both API hosts; its timeout must allow an upload chunk to go through.</param>
    public DropboxClient(IDropboxTokenSource tokenSource, HttpClient httpClient, DropboxClientOptions? options = null)
    {
        Api = new DropboxApi(httpClient, tokenSource, options ?? new DropboxClientOptions());
    }

    /// <param name="getAccessToken">
    /// Returns a valid access token; asked again after Dropbox refused one as expired, so it should refresh a
    /// token whose time is up.
    /// </param>
    public DropboxClient(Func<CancellationToken, ValueTask<string>> getAccessToken, HttpClient httpClient,
        DropboxClientOptions? options = null)
        : this(new DelegateTokenSource(getAccessToken), httpClient, options)
    {
    }

    internal DropboxApi Api { get; }

    public DropboxClientOptions Options => Api.Options;

    public string FullName(string path) => DropboxPath.Format(path);

    /// <summary>A provider for a folder: <c>""</c> or <c>dropbox:/</c> is the root, else <c>/Photos</c> or <c>dropbox:/Photos</c>.</summary>
    public DropboxFileProvider CreateProvider(string path = "") => new(this, DropboxPath.Normalize(path));

    /// <summary>The account's root with its quota (<c>users/get_space_usage</c>).</summary>
    public async Task<DropboxDriveItem> GetDriveAsync(CancellationToken ct = default) =>
        new(this, await GetSpaceUsageAsync(ct).ConfigureAwait(false));

    public async Task<DropboxSpaceUsage> GetSpaceUsageAsync(CancellationToken ct = default)
    {
        var usage = await Api.RpcAsync("users/get_space_usage", Json.ApiSpaceUsage, ct).ConfigureAwait(false);
        var allocation = usage.Allocation;

        return allocation.Tag switch
        {
            "team" when allocation.UserWithinTeamSpaceAllocated is > 0 =>
                new DropboxSpaceUsage(usage.Used, allocation.UserWithinTeamSpaceAllocated.Value, true),
            "team" => new DropboxSpaceUsage(allocation.Used ?? usage.Used, allocation.Allocated ?? 0, true),
            _ => new DropboxSpaceUsage(usage.Used, allocation.Allocated ?? 0, false)
        };
    }

    /// <summary>The file or folder at <paramref name="path"/>; null when there is none. The root has no metadata.</summary>
    public async Task<DropboxEntry?> GetEntryAsync(string path, CancellationToken ct = default)
    {
        path = DropboxPath.Normalize(path);

        if (path.Length == 0)
            throw new ArgumentException("Dropbox keeps no metadata of the root folder.", nameof(path));

        try
        {
            var metadata = await Api.RpcAsync("files/get_metadata", new ApiPathArg(path), Json.ApiPathArg,
                Json.ApiMetadata, ct).ConfigureAwait(false);
            return DropboxEntry.From(metadata, path);
        }
        catch (DropboxApiException e) when (e.IsNotFound)
        {
            return null;
        }
    }

    /// <summary>The entries of a folder, page by page (<c>list_folder</c> + <c>list_folder/continue</c>).</summary>
    public async IAsyncEnumerable<DropboxEntry> ListAsync(string folderPath,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var page = await Api.RpcAsync("files/list_folder",
            new ApiListFolderArg(DropboxPath.Normalize(folderPath), false, ListPageLimit), Json.ApiListFolderArg,
            Json.ApiListFolderResult, ct).ConfigureAwait(false);

        while (true)
        {
            foreach (var metadata in page.Entries)
            {
                if (DropboxEntry.From(metadata) is { } entry)
                    yield return entry;
            }

            if (!page.HasMore)
                yield break;

            page = await Api.RpcAsync("files/list_folder/continue", new ApiCursorArg(page.Cursor), Json.ApiCursorArg,
                Json.ApiListFolderResult, ct).ConfigureAwait(false);
        }
    }

    // ── Writes ───────────────────────────────────────────────────────────────

    internal async Task<DropboxEntry> CreateFolderAsync(string path, CancellationToken ct)
    {
        var result = await Api.RpcAsync("files/create_folder_v2", new ApiCreateFolderArg(path, false),
            Json.ApiCreateFolderArg, Json.ApiMetadataResult, ct).ConfigureAwait(false);
        return ToEntry(result.Metadata, path);
    }

    /// <summary><c>delete_v2</c>: the item goes to the account's deleted files and can be restored for its retention period.</summary>
    internal Task DeleteAsync(string path, CancellationToken ct) =>
        Api.RpcAsync("files/delete_v2", new ApiPathArg(path), Json.ApiPathArg, Json.ApiMetadataResult, ct);

    /// <summary>
    /// <c>permanently_delete</c>: Dropbox Business apps only (scope <c>files.permanent_delete</c>); anywhere else it
    /// fails. Dropbox notes the call can delete and then fail to purge, leaving the item in the deleted files.
    /// </summary>
    internal Task PermanentlyDeleteAsync(string path, CancellationToken ct) =>
        Api.RpcAsync("files/permanently_delete", new ApiPathArg(path), Json.ApiPathArg, ct);

    internal Task<DropboxEntry> CopyAsync(string from, string to, CancellationToken ct) =>
        RelocateAsync("copy", from, to, ct);

    internal Task<DropboxEntry> MoveAsync(string from, string to, CancellationToken ct) =>
        RelocateAsync("move", from, to, ct);

    /// <summary>
    /// <c>copy_v2</c> / <c>move_v2</c>. Over 10 000 files and folders Dropbox refuses that with <c>too_many_files</c>;
    /// then the same entry goes as a one-entry batch job, polled until it ends.
    /// </summary>
    private async Task<DropboxEntry> RelocateAsync(string operation, string from, string to, CancellationToken ct)
    {
        try
        {
            var result = await Api.RpcAsync($"files/{operation}_v2", new ApiRelocationArg(from, to, false),
                Json.ApiRelocationArg, Json.ApiMetadataResult, ct).ConfigureAwait(false);
            return ToEntry(result.Metadata, to);
        }
        catch (DropboxApiException e) when (e.Has("too_many_files"))
        {
            return await RelocateInJobAsync(operation, from, to, ct).ConfigureAwait(false);
        }
    }

    private async Task<DropboxEntry> RelocateInJobAsync(string operation, string from, string to, CancellationToken ct)
    {
        var status = await Api.RpcAsync($"files/{operation}_batch_v2",
            new ApiRelocationBatchArg([new ApiRelocationPath(from, to)], false), Json.ApiRelocationBatchArg,
            Json.ApiRelocationBatchStatus, ct).ConfigureAwait(false);

        var jobId = status.AsyncJobId;
        var deadline = DateTimeOffset.UtcNow + Options.JobTimeout;

        while (status.Tag is "async_job_id" or "in_progress")
        {
            if (jobId is null)
                throw new DropboxApiException(HttpStatusCode.OK, null, $"Dropbox started a {operation} job without an id.");

            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException($"Dropbox did not finish the {operation} of '{from}' in {Options.JobTimeout}.");

            await Task.Delay(Options.JobPollInterval, ct).ConfigureAwait(false);

            status = await Api.RpcAsync($"files/{operation}_batch/check_v2", new ApiAsyncJobArg(jobId),
                Json.ApiAsyncJobArg, Json.ApiRelocationBatchStatus, ct).ConfigureAwait(false);
        }

        if (status is not { Tag: "complete", Entries: [var entry] })
            throw new DropboxApiException(HttpStatusCode.Conflict, status.Tag,
                $"Dropbox could not {operation} '{from}': {status.Tag}.");

        if (entry is { Tag: "success", Success: { } metadata })
            return ToEntry(metadata, to);

        var summary = entry.Failure is { } failure ? TagPath(failure) : entry.Tag;
        throw new DropboxApiException(HttpStatusCode.Conflict, summary,
            $"Dropbox could not {operation} '{from}' to '{to}': {summary}.");
    }

    /// <summary>
    /// Uploads <paramref name="source"/> to <paramref name="path"/>. A seekable source up to
    /// <see cref="DropboxClientOptions.SimpleUploadLimit"/> goes in one <c>files/upload</c> (rewound when a retry
    /// sends it again); anything else through an upload session in chunks of
    /// <see cref="DropboxClientOptions.UploadChunkSize"/>. Dropbox commits the file only at the end, so a cancelled or
    /// failed upload leaves nothing at <paramref name="path"/> (and an existing file unchanged).
    /// <paramref name="overwrite"/> picks <c>mode: overwrite</c> over <c>add</c>; <c>autorename</c> is never used —
    /// names are resolved before.
    /// </summary>
    internal async Task<DropboxEntry> UploadAsync(string path, Stream source, long length, bool overwrite,
        IProgress<double>? progress, CancellationToken ct)
    {
        var commit = new ApiCommitInfo(path, overwrite ? "overwrite" : "add", false, false);

        if (source.CanSeek && source.Length - source.Position <= Options.SimpleUploadLimit)
        {
            var start = source.Position;
            var total = source.Length - start;
            var attempts = 0;

            var metadata = await Api.UploadAsync("files/upload", commit, Json.ApiCommitInfo, () =>
            {
                if (attempts++ > 0)
                    source.Position = start;

                var content = new StreamContent(new ProgressReadStream(source, total, progress));
                content.Headers.ContentLength = total;
                return content;
            }, Json.ApiMetadata, ct).ConfigureAwait(false);

            progress?.Report(1);
            return ToEntry(metadata, path);
        }

        return await UploadInSessionAsync(commit, source, length, progress, ct).ConfigureAwait(false);
    }

    private async Task<DropboxEntry> UploadInSessionAsync(ApiCommitInfo commit, Stream source, long length,
        IProgress<double>? progress, CancellationToken ct)
    {
        var buffer = new byte[Options.UploadChunkSize];
        string? sessionId = null;
        long offset = 0;

        while (true)
        {
            var read = await ReadChunkAsync(source, buffer, ct).ConfigureAwait(false);

            if (read < buffer.Length)
            {
                ApiMetadata metadata;

                if (sessionId is null)
                    metadata = await Api.UploadAsync("files/upload", commit, Json.ApiCommitInfo,
                        () => new ByteArrayContent(buffer, 0, read), Json.ApiMetadata, ct).ConfigureAwait(false);
                else
                    metadata = await Api.UploadAsync("files/upload_session/finish",
                        new ApiUploadSessionFinishArg(new ApiUploadSessionCursor(sessionId, offset), commit),
                        Json.ApiUploadSessionFinishArg, () => new ByteArrayContent(buffer, 0, read), Json.ApiMetadata,
                        ct).ConfigureAwait(false);

                progress?.Report(1);
                return ToEntry(metadata, commit.Path);
            }

            if (sessionId is null)
                sessionId = (await Api.UploadAsync("files/upload_session/start", new ApiUploadSessionStartArg(false),
                    Json.ApiUploadSessionStartArg, () => new ByteArrayContent(buffer, 0, read),
                    Json.ApiUploadSessionStartResult, ct).ConfigureAwait(false)).SessionId;
            else
                await Api.UploadAsync("files/upload_session/append_v2",
                    new ApiUploadSessionAppendArg(new ApiUploadSessionCursor(sessionId, offset), false),
                    Json.ApiUploadSessionAppendArg, () => new ByteArrayContent(buffer, 0, read), Json.JsonElement,
                    ct).ConfigureAwait(false);

            offset += read;

            if (length > 0)
                progress?.Report(Math.Min(1, (double)offset / length));
        }
    }

    private static async Task<int> ReadChunkAsync(Stream source, byte[] buffer, CancellationToken ct)
    {
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);

            if (read == 0)
                break;

            total += read;
        }

        return total;
    }

    // ── Reads ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The file's content, downloaded completely into a temporary file before it is handed out, so a broken
    /// transfer fails here and never looks like a short file.
    /// </summary>
    internal async Task<Stream> DownloadAsync(string path, CancellationToken ct) =>
        await TemporaryFileBuffer.CreateAsync(async (destination, token) =>
        {
            using var response = await Api.DownloadAsync("files/download", new ApiPathArg(path), Json.ApiPathArg, token)
                .ConfigureAwait(false);
            await response.Content.CopyToAsync(destination, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

    /// <summary><c>get_thumbnail_v2</c> in JPEG; null when Dropbox draws no picture of this file.</summary>
    internal async Task<Stream?> GetThumbnailAsync(string path, int size, CancellationToken ct)
    {
        var arg = new ApiThumbnailArg(new ApiPathOrLink("path", path), "jpeg", ThumbnailSize(size), "bestfit");

        try
        {
            using var response = await Api.DownloadAsync("files/get_thumbnail_v2", arg, Json.ApiThumbnailArg, ct)
                .ConfigureAwait(false);
            return new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
        }
        catch (DropboxApiException e) when (e.StatusCode == HttpStatusCode.Conflict
                                            && (e.Has("unsupported_extension") || e.Has("unsupported_image")
                                                || e.Has("conversion_error")))
        {
            return null;
        }
    }

    /// <summary>The smallest size Dropbox draws whose longer side reaches <paramref name="size"/>.</summary>
    internal static string ThumbnailSize(int size) => size switch
    {
        <= 32 => "w32h32",
        <= 64 => "w64h64",
        <= 128 => "w128h128",
        <= 256 => "w256h256",
        <= 480 => "w480h320",
        <= 640 => "w640h480",
        <= 960 => "w960h640",
        <= 1024 => "w1024h768",
        _ => "w2048h1536"
    };

    /// <summary>
    /// <c>search_v2</c> on file names below <paramref name="folder"/> (the whole Dropbox for the root), page by page
    /// through <c>search/continue_v2</c>. Dropbox's index matches words and prefixes, not any substring.
    /// </summary>
    internal async IAsyncEnumerable<DropboxEntry> SearchAsync(string folder, string query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var options = new ApiSearchOptions(folder.Length == 0 ? null : folder, SearchPageSize, "active", true);
        var page = await Api.RpcAsync("files/search_v2", new ApiSearchArg(query, options), Json.ApiSearchArg,
            Json.ApiSearchResult, ct).ConfigureAwait(false);

        while (true)
        {
            foreach (var match in page.Matches)
            {
                if (match.Metadata is { Tag: "metadata", Metadata: { } metadata }
                    && DropboxEntry.From(metadata) is { } entry
                    && DropboxPath.IsInside(entry.Path, folder)
                    && !DropboxPath.AreSame(entry.Path, folder))
                    yield return entry;
            }

            if (!page.HasMore || page.Cursor is null)
                yield break;

            page = await Api.RpcAsync("files/search/continue_v2", new ApiCursorArg(page.Cursor), Json.ApiCursorArg,
                Json.ApiSearchResult, ct).ConfigureAwait(false);
        }
    }

    // ── Shared links ─────────────────────────────────────────────────────────

    /// <summary>
    /// A new view-only link anyone can open (<c>audience: public</c>, <c>access: viewer</c>). When the item already
    /// has a link, that one is returned as it is: from the error's metadata, or from <c>list_shared_links</c>.
    /// </summary>
    internal async Task<Uri> GetPublicLinkAsync(string path, CancellationToken ct)
    {
        try
        {
            var link = await Api.RpcAsync("sharing/create_shared_link_with_settings",
                new ApiCreateSharedLinkArg(path, new ApiSharedLinkSettings("public", "viewer")),
                Json.ApiCreateSharedLinkArg, Json.ApiSharedLink, ct).ConfigureAwait(false);
            return new Uri(link.Url);
        }
        catch (DropboxApiException e) when (e.Has("shared_link_already_exists"))
        {
            if (ExistingLinkUrl(e.Error) is { } url)
                return new Uri(url);

            var links = await ListDirectLinksAsync(path, ct).ConfigureAwait(false);

            if (links.Count > 0)
                return new Uri(links[0]);

            throw;
        }
    }

    /// <summary>Revokes every link of the item itself (<c>direct_only</c>); links of its parents stay.</summary>
    internal async Task RemovePublicLinksAsync(string path, CancellationToken ct)
    {
        foreach (var url in await ListDirectLinksAsync(path, ct).ConfigureAwait(false))
        {
            try
            {
                await Api.RpcAsync("sharing/revoke_shared_link", new ApiRevokeSharedLinkArg(url),
                    Json.ApiRevokeSharedLinkArg, ct).ConfigureAwait(false);
            }
            catch (DropboxApiException e) when (e.Has("shared_link_not_found"))
            {
            }
        }
    }

    private async Task<List<string>> ListDirectLinksAsync(string path, CancellationToken ct)
    {
        var urls = new List<string>();
        string? cursor = null;

        do
        {
            var page = await Api.RpcAsync("sharing/list_shared_links", new ApiListSharedLinksArg(path, true, cursor),
                Json.ApiListSharedLinksArg, Json.ApiListSharedLinksResult, ct).ConfigureAwait(false);

            urls.AddRange(page.Links
                .Where(l => l.PathLower is null || DropboxPath.AreSame(l.PathLower, path))
                .Select(l => l.Url));

            cursor = page.HasMore ? page.Cursor : null;
        }
        while (cursor is not null);

        return urls;
    }

    private static string? ExistingLinkUrl(JsonElement? error) =>
        error is { ValueKind: JsonValueKind.Object } e
        && e.TryGetProperty("shared_link_already_exists", out var exists)
        && exists.ValueKind == JsonValueKind.Object
        && exists.TryGetProperty("metadata", out var metadata)
        && metadata.ValueKind == JsonValueKind.Object
        && metadata.TryGetProperty("url", out var url)
            ? url.GetString()
            : null;

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DropboxEntry ToEntry(ApiMetadata metadata, string path) =>
        DropboxEntry.From(metadata, path)
        ?? throw new DropboxApiException(HttpStatusCode.OK, null, $"Dropbox answered with a {metadata.Tag} entry for '{path}'.");

    /// <summary>The chain of union tags of an error, as Dropbox writes <c>error_summary</c>: <c>relocation_error/to/conflict/folder</c>.</summary>
    internal static string TagPath(JsonElement error)
    {
        var builder = new StringBuilder();
        var current = error;

        while (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(".tag", out var tag)
               && tag.GetString() is { } name)
        {
            builder.Append(builder.Length == 0 ? "" : "/").Append(name);

            if (!current.TryGetProperty(name, out current))
                break;
        }

        return builder.ToString();
    }
}
