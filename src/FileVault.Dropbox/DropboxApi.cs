using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace FileVault.Dropbox;

/// <summary>
/// A Dropbox API call failed. <see cref="ErrorSummary"/> is Dropbox's machine-readable error path
/// (<c>path/not_found/..</c>, <c>to/conflict/file/..</c>); <see cref="StatusCode"/> 409 is an endpoint error,
/// 401 the token, 429 a rate limit (see <see cref="RetryAfter"/>).
/// </summary>
public sealed class DropboxApiException : IOException
{
    internal DropboxApiException(HttpStatusCode statusCode, string? errorSummary, string message,
        TimeSpan? retryAfter = null, JsonElement? error = null) : base(message)
    {
        StatusCode = statusCode;
        ErrorSummary = errorSummary;
        RetryAfter = retryAfter;
        Error = error;
    }

    public HttpStatusCode StatusCode { get; }
    public string? ErrorSummary { get; }
    public TimeSpan? RetryAfter { get; }

    internal JsonElement? Error { get; }

    /// <summary>The first tag of the error, e.g. <c>path</c>, <c>to</c>, <c>expired_access_token</c>.</summary>
    public string? Tag => ErrorSummary?.Split('/')[0].TrimEnd('.');

    /// <summary>The path or item does not exist (<c>path/not_found</c>, <c>from_lookup/not_found</c>, …).</summary>
    public bool IsNotFound => StatusCode == HttpStatusCode.Conflict && Has("not_found");

    /// <summary>The error summary holds <paramref name="tag"/> as one of its segments.</summary>
    public bool Has(string tag) =>
        ErrorSummary is { } summary && summary.Split('/').Any(s => s.TrimEnd('.') == tag);

    /// <summary>The error summary holds these consecutive segments, e.g. <c>to/conflict/file</c>.</summary>
    public bool HasPath(string segments) =>
        ErrorSummary is { } summary && ("/" + summary + "/").Contains("/" + segments + "/", StringComparison.Ordinal);
}

/// <summary>
/// The HTTP side of Dropbox: RPC calls with a JSON body on the API host, content calls with the argument in the
/// <c>Dropbox-API-Arg</c> header on the content host. Every call carries a token from the app, is retried once with
/// a fresh token after 401 <c>expired_access_token</c>, and waits out 429/503 <c>Retry-After</c> a bounded number
/// of times.
/// </summary>
internal sealed class DropboxApi(HttpClient http, IDropboxTokenSource tokens, DropboxClientOptions options)
{
    internal const string ArgHeader = "Dropbox-API-Arg";
    internal const string ResultHeader = "Dropbox-API-Result";

    public DropboxClientOptions Options => options;

    // ── RPC endpoints (api host) ─────────────────────────────────────────────

    public async Task<TResponse> RpcAsync<TArg, TResponse>(string route, TArg arg, JsonTypeInfo<TArg> argType,
        JsonTypeInfo<TResponse> responseType, CancellationToken ct)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, new Uri(options.ApiBaseUri, route))
            {
                Content = JsonContent.Create(arg, argType)
            }, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

        return await ReadJsonAsync(response, route, responseType, ct).ConfigureAwait(false);
    }

    /// <summary>A route whose result is void (Dropbox answers <c>null</c>).</summary>
    public async Task RpcAsync<TArg>(string route, TArg arg, JsonTypeInfo<TArg> argType, CancellationToken ct)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, new Uri(options.ApiBaseUri, route))
            {
                Content = JsonContent.Create(arg, argType)
            }, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
    }

    /// <summary>A route without an argument: no body at all, as Dropbox expects.</summary>
    public async Task<TResponse> RpcAsync<TResponse>(string route, JsonTypeInfo<TResponse> responseType,
        CancellationToken ct)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, new Uri(options.ApiBaseUri, route)),
            HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

        return await ReadJsonAsync(response, route, responseType, ct).ConfigureAwait(false);
    }

    // ── Content endpoints (content host) ─────────────────────────────────────

    /// <summary>
    /// An upload-style call. <paramref name="body"/> is asked for once per attempt, so a retry sends the content
    /// again; a body that cannot be sent twice throws on its second call.
    /// </summary>
    public async Task<TResponse> UploadAsync<TArg, TResponse>(string route, TArg arg, JsonTypeInfo<TArg> argType,
        Func<HttpContent> body, JsonTypeInfo<TResponse> responseType, CancellationToken ct)
    {
        var header = EncodeArg(arg, argType);

        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.ContentBaseUri, route));
            request.Headers.TryAddWithoutValidation(ArgHeader, header);
            var content = body();
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content = content;
            return request;
        }, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

        return await ReadJsonAsync(response, route, responseType, ct).ConfigureAwait(false);
    }

    /// <summary>A download-style call: the response with the content; the caller disposes it.</summary>
    public Task<HttpResponseMessage> DownloadAsync<TArg>(string route, TArg arg, JsonTypeInfo<TArg> argType,
        CancellationToken ct)
    {
        var header = EncodeArg(arg, argType);

        return SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.ContentBaseUri, route));
            request.Headers.TryAddWithoutValidation(ArgHeader, header);
            return request;
        }, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>
    /// The JSON of <c>Dropbox-API-Arg</c>. HTTP headers carry ASCII only, so every character outside printable
    /// ASCII is written as a JSON <c>\uXXXX</c> escape (UTF-16 units, surrogate pairs as two escapes). Outside of
    /// strings JSON holds ASCII only, so escaping the whole text is safe.
    /// </summary>
    internal static string EncodeArg<TArg>(TArg arg, JsonTypeInfo<TArg> argType) =>
        EscapeNonAscii(JsonSerializer.Serialize(arg, argType));

    internal static string EscapeNonAscii(string json)
    {
        StringBuilder? builder = null;

        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];

            if (c is >= ' ' and < (char)0x7F)
            {
                builder?.Append(c);
                continue;
            }

            builder ??= new StringBuilder(json.Length + 16).Append(json, 0, i);
            builder.Append("\\u").Append(((int)c).ToString("x4"));
        }

        return builder?.ToString() ?? json;
    }

    // ── Sending ──────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, HttpCompletionOption completion,
        CancellationToken ct)
    {
        string? rejected = null;
        DropboxApiException? expired = null;
        var rateLimitRetries = 0;

        while (true)
        {
            var token = await tokens.GetAccessTokenAsync(rejected, ct).ConfigureAwait(false);

            if (expired is not null && token == rejected)
                throw expired;

            rejected = null;
            HttpResponseMessage response;

            using (var request = build())
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                response = await http.SendAsync(request, completion, ct).ConfigureAwait(false);
            }

            if (response.IsSuccessStatusCode)
                return response;

            DropboxApiException error;

            using (response)
                error = await ReadErrorAsync(response, ct).ConfigureAwait(false);

            if (error.StatusCode == HttpStatusCode.Unauthorized && error.Has("expired_access_token") && expired is null)
            {
                expired = error;
                rejected = token;
                continue;
            }

            if (error.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                && rateLimitRetries < options.MaxRateLimitRetries
                && (error.RetryAfter ?? TimeSpan.Zero) <= options.MaxRetryAfter)
            {
                rateLimitRetries++;
                await Task.Delay(error.RetryAfter ?? options.DefaultRetryAfter, ct).ConfigureAwait(false);
                continue;
            }

            throw error;
        }
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, string route, JsonTypeInfo<T> type,
        CancellationToken ct) =>
        await response.Content.ReadFromJsonAsync(type, ct).ConfigureAwait(false)
        ?? throw new DropboxApiException(response.StatusCode, null, $"Dropbox returned an empty answer for {route}.");

    internal static async Task<DropboxApiException> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ApiErrorBody? parsed = null;

        try
        {
            if (body.TrimStart().StartsWith('{'))
                parsed = JsonSerializer.Deserialize(body, DropboxJsonContext.Default.ApiErrorBody);
        }
        catch (JsonException)
        {
        }

        var retryAfter = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => (TimeSpan?)null
        };

        if (retryAfter is null && parsed?.Error is { ValueKind: JsonValueKind.Object } error
            && error.TryGetProperty("retry_after", out var seconds) && seconds.TryGetInt32(out var value))
            retryAfter = TimeSpan.FromSeconds(value);

        if (retryAfter < TimeSpan.Zero)
            retryAfter = TimeSpan.Zero;

        var detail = parsed?.ErrorSummary ?? (body.Length > 300 ? body[..300] : body);

        return new DropboxApiException(response.StatusCode, parsed?.ErrorSummary,
            $"Dropbox answered {(int)response.StatusCode} {response.ReasonPhrase}{(string.IsNullOrWhiteSpace(detail) ? "" : $": {detail.Trim()}")}",
            retryAfter, parsed?.Error);
    }
}
