using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileVault.Dropbox;

/// <summary>Dropbox refused an OAuth step; <see cref="Error"/> is the OAuth error code (<c>invalid_grant</c>, <c>access_denied</c>, …).</summary>
public sealed class DropboxAuthException(string error, string? description)
    : Exception(description is null ? $"Dropbox sign-in failed: {error}" : $"Dropbox sign-in failed: {error} — {description}")
{
    public string Error { get; } = error;
    public string? Description { get; } = description;
}

/// <summary>
/// The tokens of one signed-in account. <see cref="ExpiresAt"/> is when the short-lived access token stops working
/// (<see cref="DateTimeOffset.MaxValue"/> for a token without an expiry). <see cref="RefreshToken"/> does not expire
/// on its own; it stops working when the user or the app revokes it. Store it as a secret.
/// </summary>
public sealed record DropboxTokens(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAt,
    string? AccountId,
    string? Scope);

/// <summary>
/// One sign-in attempt: the page to open in the browser and what the code exchange needs to finish it.
/// Keep it until the code comes back; <see cref="CodeVerifier"/> never leaves the app.
/// </summary>
public sealed record DropboxAuthorizationRequest(Uri AuthorizeUri, string CodeVerifier, string State, Uri? RedirectUri);

/// <summary>
/// OAuth 2 authorization code flow with PKCE for a public client — the app key only, no app secret — asking for
/// offline access, so the answer carries a refresh token. Two ways to get the code back:
/// <list type="bullet">
/// <item>Paste: no redirect URI. Dropbox shows the code after the user allows access; the user pastes it into the
/// app, which passes it to <see cref="ExchangeCodeAsync"/>.</item>
/// <item>Loopback: a redirect URI such as <c>http://127.0.0.1:53682/dropbox</c>, registered exactly (port and path
/// included) in the App Console. The app listens there and hands the URI Dropbox opened to
/// <see cref="ParseRedirect"/>.</item>
/// </list>
/// </summary>
public sealed class DropboxAuth
{
    public static readonly Uri DefaultAuthorizeUri = new("https://www.dropbox.com/oauth2/authorize");
    public static readonly Uri DefaultTokenUri = new("https://api.dropboxapi.com/oauth2/token");
    public static readonly Uri DefaultRevokeUri = new("https://api.dropboxapi.com/2/auth/token/revoke");

    private readonly HttpClient _http;

    public DropboxAuth(string appKey, HttpClient httpClient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);
        AppKey = appKey;
        _http = httpClient;
    }

    public string AppKey { get; }

    public Uri AuthorizeUri { get; init; } = DefaultAuthorizeUri;
    public Uri TokenUri { get; init; } = DefaultTokenUri;
    public Uri RevokeUri { get; init; } = DefaultRevokeUri;

    /// <summary>For tests: the clock the expiry of new tokens is counted from.</summary>
    internal Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// A new sign-in: a fresh PKCE verifier (S256 challenge) and state, <c>token_access_type=offline</c>.
    /// </summary>
    /// <param name="redirectUri">Null for the paste flow; else a URI registered in the App Console.</param>
    /// <param name="scopes">A subset of the scopes enabled in the App Console; null asks for all of them.</param>
    /// <param name="forceReapprove">Show the consent page even when the user approved the app before.</param>
    public DropboxAuthorizationRequest CreateAuthorizationRequest(Uri? redirectUri = null,
        IReadOnlyCollection<string>? scopes = null, bool forceReapprove = false)
    {
        var verifier = CreateCodeVerifier();
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var query = new List<KeyValuePair<string, string>>
        {
            new("client_id", AppKey),
            new("response_type", "code"),
            new("code_challenge", CreateCodeChallenge(verifier)),
            new("code_challenge_method", "S256"),
            new("token_access_type", "offline"),
            new("state", state)
        };

        if (redirectUri is not null)
            query.Add(new("redirect_uri", redirectUri.OriginalString));

        if (scopes is { Count: > 0 })
            query.Add(new("scope", string.Join(' ', scopes)));

        if (forceReapprove)
            query.Add(new("force_reapprove", "true"));

        var builder = new UriBuilder(AuthorizeUri)
        {
            Query = string.Join('&', query.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"))
        };

        return new DropboxAuthorizationRequest(builder.Uri, verifier, state, redirectUri);
    }

    /// <summary>
    /// The code from the URI Dropbox redirected the browser to. Throws <see cref="DropboxAuthException"/> when the
    /// user declined (<c>access_denied</c>) or the state does not match this request (a forged or stale redirect).
    /// </summary>
    public static string ParseRedirect(DropboxAuthorizationRequest request, Uri redirectedTo)
    {
        var query = ParseQuery(redirectedTo.Query);

        if (!query.TryGetValue("state", out var state) || state != request.State)
            throw new DropboxAuthException("invalid_state", "The redirect does not belong to this sign-in.");

        if (query.TryGetValue("error", out var error))
            throw new DropboxAuthException(error, query.GetValueOrDefault("error_description"));

        return query.TryGetValue("code", out var code) && code.Length > 0
            ? code
            : throw new DropboxAuthException("invalid_request", "The redirect carries no code.");
    }

    /// <summary>
    /// Exchanges the code (pasted by the user or from <see cref="ParseRedirect"/>) for tokens: <c>code</c>,
    /// <c>client_id</c>, <c>code_verifier</c>, and the same <c>redirect_uri</c> when the request had one.
    /// </summary>
    public async Task<DropboxTokens> ExchangeCodeAsync(DropboxAuthorizationRequest request, string code,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("code", code.Trim()),
            new("client_id", AppKey),
            new("code_verifier", request.CodeVerifier)
        };

        if (request.RedirectUri is not null)
            form.Add(new("redirect_uri", request.RedirectUri.OriginalString));

        return await RequestTokensAsync(form, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A new access token for a refresh token (<c>grant_type=refresh_token</c>, <c>client_id</c>, no secret). The
    /// refresh token stays the same and is carried over into the result.
    /// </summary>
    public async Task<DropboxTokens> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        return await RequestTokensAsync([
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken),
            new("client_id", AppKey)
        ], refreshToken, ct).ConfigureAwait(false);
    }

    /// <summary>Signs out: revokes the access token and the refresh token it came with (<c>auth/token/revoke</c>).</summary>
    public async Task RevokeAsync(string accessToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RevokeUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw await DropboxApi.ReadErrorAsync(response, ct).ConfigureAwait(false);
    }

    // ── PKCE ─────────────────────────────────────────────────────────────────

    /// <summary>86 characters of base64url: inside the 43–128 PKCE allows, from its unreserved alphabet.</summary>
    internal static string CreateCodeVerifier() => Base64Url(RandomNumberGenerator.GetBytes(64));

    internal static string CreateCodeChallenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<DropboxTokens> RequestTokensAsync(List<KeyValuePair<string, string>> form, string? refreshToken,
        CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(TokenUri, content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            ApiOAuthError? error = null;

            try
            {
                error = JsonSerializer.Deserialize(body, DropboxJsonContext.Default.ApiOAuthError);
            }
            catch (JsonException)
            {
            }

            throw new DropboxAuthException(error?.Error ?? $"http_{(int)response.StatusCode}",
                error?.ErrorDescription ?? (body.Length > 300 ? body[..300] : body));
        }

        var tokens = JsonSerializer.Deserialize(body, DropboxJsonContext.Default.ApiTokenResponse)
                     ?? throw new DropboxAuthException("invalid_response", "Dropbox returned no tokens.");

        return new DropboxTokens(
            tokens.AccessToken,
            tokens.RefreshToken ?? refreshToken,
            tokens.ExpiresIn is { } seconds ? Now() + TimeSpan.FromSeconds(seconds) : DateTimeOffset.MaxValue,
            tokens.AccountId,
            tokens.Scope);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((equals < 0 ? pair : pair[..equals]).Replace('+', ' '));
            var value = equals < 0 ? "" : Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            result.TryAdd(key, value);
        }

        return result;
    }
}
