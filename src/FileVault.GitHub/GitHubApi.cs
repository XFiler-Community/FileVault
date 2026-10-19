using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace FileVault.GitHub;

/// <summary>A GitHub API call failed; <see cref="StatusCode"/> tells why (401 — the token, 403 — access or rate limit).</summary>
public sealed class GitHubApiException(HttpStatusCode statusCode, string message) : IOException(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

/// <summary>REST calls for one repository. Every response is checked; JSON goes through source-generated metadata.</summary>
internal sealed class GitHubApi
{
    private const string JsonMediaType = "application/vnd.github+json";
    private const string RawMediaType = "application/vnd.github.raw+json";
    private const string LfsMediaType = "application/vnd.git-lfs+json";

    private readonly HttpClient _http;
    private readonly string? _token;
    private readonly Uri _apiBase;
    private readonly Uri _lfsBase;
    private readonly string _repoPath;

    public GitHubApi(HttpClient http, string? token, Uri apiBase, Uri webBase, string owner, string name)
    {
        _http = http;
        _token = token;
        _apiBase = apiBase;
        _repoPath = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}";
        _lfsBase = new Uri(webBase, $"{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}.git/info/lfs/");
    }

    public static string EscapePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    public Task<T> GetAsync<T>(string relative, JsonTypeInfo<T> type, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, relative, null, type, ct);

    /// <summary>Null when the API answers 404 (no such branch, commit or path).</summary>
    public async Task<T?> TryGetAsync<T>(string relative, JsonTypeInfo<T> type, CancellationToken ct) where T : class
    {
        try
        {
            return await GetAsync(relative, type, ct).ConfigureAwait(false);
        }
        catch (GitHubApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task<TResponse> PostAsync<TRequest, TResponse>(string relative, TRequest body,
        JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResponse> responseType, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, relative, JsonContent.Create(body, requestType), responseType, ct);

    public Task<TResponse> PatchAsync<TRequest, TResponse>(string relative, TRequest body,
        JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResponse> responseType, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, relative, JsonContent.Create(body, requestType), responseType, ct);

    /// <summary>Writes the raw bytes of a blob into <paramref name="destination"/>.</summary>
    public async Task DownloadBlobAsync(string sha, Stream destination, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, $"{_repoPath}/git/blobs/{sha}", RawMediaType);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        await response.Content.CopyToAsync(destination, ct).ConfigureAwait(false);
    }

    /// <summary>Downloads a Git LFS object through the batch API and the storage URL it hands out.</summary>
    public async Task DownloadLfsObjectAsync(GitLfsPointer pointer, Stream destination, CancellationToken ct)
    {
        var batch = new LfsBatchRequest("download", ["basic"], [new LfsObjectRequest(pointer.Oid, pointer.Size)]);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_lfsBase, "objects/batch"));
        request.Headers.Accept.ParseAdd(LfsMediaType);
        request.Content = JsonContent.Create(batch, GitHubJsonContext.Default.LfsBatchRequest,
            new MediaTypeHeaderValue(LfsMediaType));

        // Git LFS takes the token as the password of basic auth; the user name is not checked
        if (_token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{_token}")));

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        var answer = await response.Content.ReadFromJsonAsync(GitHubJsonContext.Default.LfsBatchResponse, ct)
            .ConfigureAwait(false);
        var lfsObject = answer?.Objects.FirstOrDefault(o => o.Oid == pointer.Oid)
            ?? throw new GitHubApiException(HttpStatusCode.NotFound, $"Git LFS has no object {pointer.Oid}.");

        if (lfsObject.Error is { } error)
            throw new GitHubApiException((HttpStatusCode)error.Code, $"Git LFS: {error.Message}");

        var download = lfsObject.Actions?.Download
            ?? throw new GitHubApiException(HttpStatusCode.NotFound, $"Git LFS gave no download for {pointer.Oid}.");

        // The storage URL is signed; only the headers LFS returned go with it, never the GitHub token
        using var objectRequest = new HttpRequestMessage(HttpMethod.Get, download.Href);
        foreach (var (header, value) in download.Header ?? [])
            objectRequest.Headers.TryAddWithoutValidation(header, value);

        using var objectResponse = await _http.SendAsync(objectRequest, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(objectResponse, ct).ConfigureAwait(false);
        await objectResponse.Content.CopyToAsync(destination, ct).ConfigureAwait(false);
    }

    public string Repo(string relative) => relative.Length == 0 ? _repoPath : $"{_repoPath}/{relative}";

    private async Task<T> SendAsync<T>(HttpMethod method, string relative, HttpContent? content, JsonTypeInfo<T> type,
        CancellationToken ct)
    {
        using var request = CreateRequest(method, relative, JsonMediaType);
        request.Content = content;

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        return await response.Content.ReadFromJsonAsync(type, ct).ConfigureAwait(false)
               ?? throw new GitHubApiException(response.StatusCode, $"GitHub returned an empty answer for {relative}.");
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relative, string accept)
    {
        var request = new HttpRequestMessage(method, new Uri(_apiBase, relative));
        request.Headers.Accept.ParseAdd(accept);
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        if (request.Headers.UserAgent.Count == 0)
            request.Headers.UserAgent.ParseAdd("FileVault.GitHub");

        if (_token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        string? message = null;

        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            message = JsonSerializer.Deserialize(body, GitHubJsonContext.Default.ApiError)?.Message;
        }
        catch (JsonException)
        {
        }

        throw new GitHubApiException(response.StatusCode,
            $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}{(message is null ? "" : $": {message}")}");
    }
}
