using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace FileVault.OneDrive;

public sealed partial class OneDriveClient
{
    /// <summary>The wait between retries; replaced in tests so throttling costs no time.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    internal Uri Resolve(string relativeOrAbsolute) =>
        relativeOrAbsolute.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || relativeOrAbsolute.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            ? new Uri(relativeOrAbsolute)
            : new Uri(_options.GraphBaseAddress, relativeOrAbsolute);

    /// <summary>
    /// Sends a request built by <paramref name="create"/> (called again for every attempt, since a sent request cannot
    /// be reused). With <paramref name="authorize"/> it carries the bearer token, and a 401 asks the token source for a
    /// new token once. 429 and 503 — and with <paramref name="retryServerErrors"/> 500/502/504 — are retried after
    /// <c>Retry-After</c>, a bounded number of times. The last answer is returned as it is, checked by the caller.
    /// </summary>
    internal async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> create, bool authorize,
        bool retryServerErrors, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var tokenRefreshed = false;
        var forceRefresh = false;
        var retries = 0;

        while (true)
        {
            var request = create();

            if (authorize)
            {
                var token = await _accessToken(forceRefresh, ct).ConfigureAwait(false);
                forceRefresh = false;
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            var response = await _http.SendAsync(request, completion, ct).ConfigureAwait(false);

            if (authorize && response.StatusCode == HttpStatusCode.Unauthorized && !tokenRefreshed)
            {
                response.Dispose();
                tokenRefreshed = true;
                forceRefresh = true;
                continue;
            }

            if (retries < _options.MaxRetries && IsRetryable(response.StatusCode, retryServerErrors))
            {
                var delay = RetryDelay(response, retries);
                response.Dispose();
                retries++;
                await Delay(delay, ct).ConfigureAwait(false);
                continue;
            }

            return response;
        }
    }

    private static bool IsRetryable(HttpStatusCode status, bool serverErrors) => status switch
    {
        HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => true,
        HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout => serverErrors,
        _ => false
    };

    private TimeSpan RetryDelay(HttpResponseMessage response, int retries)
    {
        var delay = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => TimeSpan.FromSeconds(Math.Pow(2, retries))
        };

        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;

        return delay > _options.MaxRetryDelay ? _options.MaxRetryDelay : delay;
    }

    internal async Task<T> GetJsonAsync<T>(string relative, JsonTypeInfo<T> type, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Resolve(relative)), true, false,
            ct).ConfigureAwait(false);
        return await ReadJsonAsync(response, type, ct).ConfigureAwait(false);
    }

    /// <summary>Null when Graph answers 404 (no such item); every other failure throws.</summary>
    internal async Task<T?> TryGetJsonAsync<T>(string relative, JsonTypeInfo<T> type, CancellationToken ct) where T : class
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Resolve(relative)), true, false,
            ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        return await ReadJsonAsync(response, type, ct).ConfigureAwait(false);
    }

    internal async Task<TResponse> SendJsonAsync<TRequest, TResponse>(HttpMethod method, string relative, TRequest body,
        JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResponse> responseType, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(method, Resolve(relative))
        {
            Content = JsonContent.Create(body, requestType)
        }, true, false, ct).ConfigureAwait(false);

        return await ReadJsonAsync(response, responseType, ct).ConfigureAwait(false);
    }

    internal async Task SendWithoutBodyAsync(HttpMethod method, string relative, CancellationToken ct)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(method, Resolve(relative)), true, false, ct)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    internal static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type,
        CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);

        return await response.Content.ReadFromJsonAsync(type, ct).ConfigureAwait(false)
               ?? throw new OneDriveApiException(response.StatusCode, null,
                   $"Microsoft Graph returned an empty answer for {response.RequestMessage?.RequestUri}.");
    }

    internal static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        GraphError? error = null;

        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (body.Length > 0)
                error = JsonSerializer.Deserialize(body, OneDriveJsonContext.Default.GraphErrorBody)?.Error;
        }
        catch (JsonException)
        {
        }

        throw new OneDriveApiException(response.StatusCode, error?.Code,
            $"Microsoft Graph answered {(int)response.StatusCode} {response.ReasonPhrase}"
            + (error?.Message is { } message ? $": {message}" : ""));
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.Moved or HttpStatusCode.Found
        or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>
    /// GETs a Graph URL whose answer may be a redirect to pre-authenticated storage (file content, thumbnails). The
    /// default handler follows it by itself and drops the Authorization header; a handler that does not follow
    /// redirects hands it here, and it is followed the same way — never with the token.
    /// </summary>
    internal async Task<HttpResponseMessage> GetFollowingRedirectAsync(string relative, CancellationToken ct)
    {
        var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Resolve(relative)), true, false, ct,
            HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

        for (var hops = 0; IsRedirect(response.StatusCode) && response.Headers.Location is { } location && hops < 5; hops++)
        {
            var target = location.IsAbsoluteUri ? location : new Uri(response.RequestMessage!.RequestUri!, location);
            response.Dispose();
            response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, target), false, true, ct,
                HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        }

        return response;
    }
}
