using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileVault.OneDrive;

/// <summary>
/// What <see cref="OneDriveAuth.CreateAuthorizationRequest"/> made: open <see cref="Url"/> in the browser, keep the rest
/// until the redirect comes back. <see cref="CodeVerifier"/> is the PKCE secret: it never leaves the app until the code
/// is redeemed.
/// </summary>
public sealed record OneDriveAuthorizationRequest(Uri Url, Uri RedirectUri, string State, string CodeVerifier);

/// <summary>
/// Tokens of one account. The refresh token rotates: every refresh may return a new one, which replaces the old one
/// in the app's secure storage.
/// </summary>
public sealed record OneDriveTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string? Scope);

/// <summary>The identity platform refused (<c>invalid_grant</c> — sign in again, <c>access_denied</c>, …).</summary>
public sealed class OneDriveAuthException(string error, string? description)
    : Exception(description is null ? error : $"{error}: {description}")
{
    public string Error { get; } = error;
    public string? Description { get; } = description;
}

/// <summary>
/// OAuth 2.0 authorization code flow with PKCE for a public client (a desktop app: no client secret) against the
/// Microsoft identity platform, authority <c>common</c> by default — personal Microsoft accounts and work/school
/// accounts. The app registers itself in Microsoft Entra and passes its client id; nothing is built in.
/// </summary>
/// <remarks>
/// Flow: <see cref="CreateAuthorizationRequest"/> → open the URL in the system browser, listen on the loopback redirect
/// URI → <see cref="GetAuthorizationCode"/> on the URL the browser came back to → <see cref="RedeemCodeAsync"/> →
/// store <see cref="OneDriveTokens.RefreshToken"/> securely → later <see cref="RefreshAsync"/> (or let
/// <see cref="OneDriveTokenSource"/> do it).
/// </remarks>
public sealed class OneDriveAuth
{
    /// <summary>Read/write the user's files, keep access without the user (refresh token), read the profile.</summary>
    public const string DefaultScopes = "Files.ReadWrite offline_access User.Read";

    private readonly HttpClient _http;
    private readonly Uri _authorizeEndpoint;
    private readonly Uri _tokenEndpoint;

    /// <param name="http">Used for the token endpoint only.</param>
    /// <param name="clientId">The app's Application (client) id from its Entra registration.</param>
    /// <param name="tenant"><c>common</c> (any account), <c>consumers</c>, <c>organizations</c> or a tenant id.</param>
    /// <param name="scopes">Space-separated delegated scopes.</param>
    /// <param name="authorityHost">The login host; another only for national clouds.</param>
    public OneDriveAuth(HttpClient http, string clientId, string tenant = "common", string scopes = DefaultScopes,
        Uri? authorityHost = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);

        _http = http;
        ClientId = clientId;
        Scopes = scopes;

        var authority = new Uri(authorityHost ?? new Uri("https://login.microsoftonline.com/"),
            $"{Uri.EscapeDataString(tenant)}/oauth2/v2.0/");
        _authorizeEndpoint = new Uri(authority, "authorize");
        _tokenEndpoint = new Uri(authority, "token");
    }

    public string ClientId { get; }
    public string Scopes { get; }

    /// <summary>
    /// The sign-in URL with a fresh state and PKCE pair (S256). <paramref name="redirectUri"/> is the app's loopback
    /// listener, e.g. <c>http://localhost:53682/</c>: the identity platform ignores the port of a localhost redirect, so
    /// registering <c>http://localhost</c> once lets the app pick any free port.
    /// </summary>
    /// <param name="prompt"><c>select_account</c> (default) lets the user pick or add an account; null — the platform decides.</param>
    public OneDriveAuthorizationRequest CreateAuthorizationRequest(Uri redirectUri, string? loginHint = null,
        string? prompt = "select_account")
    {
        ArgumentNullException.ThrowIfNull(redirectUri);

        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));

        var query = new List<KeyValuePair<string, string>>
        {
            new("client_id", ClientId),
            new("response_type", "code"),
            new("redirect_uri", redirectUri.OriginalString),
            new("response_mode", "query"),
            new("scope", Scopes),
            new("state", state),
            new("code_challenge", CodeChallenge(verifier)),
            new("code_challenge_method", "S256")
        };

        if (prompt is not null)
            query.Add(new("prompt", prompt));

        if (loginHint is not null)
            query.Add(new("login_hint", loginHint));

        var url = new UriBuilder(_authorizeEndpoint) { Query = string.Join('&', query.Select(Pair)) }.Uri;

        return new OneDriveAuthorizationRequest(url, redirectUri, state, verifier);
    }

    /// <summary>
    /// The code from the URL the browser was redirected to. Throws <see cref="OneDriveAuthException"/> when the
    /// platform sent an error (the user declined: <c>access_denied</c>) or the state is not the request's (a forged or
    /// stale redirect: <c>state_mismatch</c>).
    /// </summary>
    public static string GetAuthorizationCode(OneDriveAuthorizationRequest request, Uri callbackUri)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(callbackUri);

        var parameters = ParseQuery(callbackUri.Query);

        if (parameters.TryGetValue("error", out var error))
            throw new OneDriveAuthException(error, parameters.GetValueOrDefault("error_description"));

        if (parameters.GetValueOrDefault("state") != request.State)
            throw new OneDriveAuthException("state_mismatch", "The redirect does not belong to this sign-in.");

        return parameters.TryGetValue("code", out var code) && code.Length > 0
            ? code
            : throw new OneDriveAuthException("missing_code", "The redirect carries no authorization code.");
    }

    /// <summary>Redeems the code with the request's PKCE verifier and redirect URI.</summary>
    public Task<OneDriveTokens> RedeemCodeAsync(OneDriveAuthorizationRequest request, string code,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(code);

        return RequestTokensAsync([
            new("client_id", ClientId),
            new("grant_type", "authorization_code"),
            new("code", code),
            new("redirect_uri", request.RedirectUri.OriginalString),
            new("code_verifier", request.CodeVerifier),
            new("scope", Scopes)
        ], null, ct);
    }

    /// <summary>
    /// New tokens for a refresh token. The answer's refresh token replaces the old one; when the platform sends none,
    /// the old one is returned again. <c>invalid_grant</c> (revoked, expired, password changed) means signing in again.
    /// </summary>
    public Task<OneDriveTokens> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(refreshToken);

        return RequestTokensAsync([
            new("client_id", ClientId),
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken),
            new("scope", Scopes)
        ], refreshToken, ct);
    }

    private async Task<OneDriveTokens> RequestTokensAsync(IEnumerable<KeyValuePair<string, string>> form,
        string? previousRefreshToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        TokenResponse? answer = null;

        try
        {
            answer = await response.Content.ReadFromJsonAsync(OneDriveAuthJsonContext.Default.TokenResponse, ct)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
        }

        if (answer?.Error is { } error)
            throw new OneDriveAuthException(error, answer.ErrorDescription);

        if (!response.IsSuccessStatusCode || answer?.AccessToken is not { Length: > 0 } accessToken)
            throw new OneDriveAuthException("invalid_response",
                $"The token endpoint answered {(int)response.StatusCode} {response.ReasonPhrase}.");

        return new OneDriveTokens(accessToken, answer.RefreshToken ?? previousRefreshToken,
            DateTimeOffset.UtcNow.AddSeconds(answer.ExpiresIn ?? 3600), answer.Scope);
    }

    internal static string CodeChallenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Pair(KeyValuePair<string, string> p) =>
        $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}";

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? part : part[..eq]).Replace('+', ' '));
            var value = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            result.TryAdd(key, value);
        }

        return result;
    }
}
