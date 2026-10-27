namespace FileVault.Dropbox;

/// <summary>
/// Where <see cref="DropboxClient"/> takes its access token from. The app owns the tokens (and where they are
/// stored); the client only asks for one before each request.
/// </summary>
public interface IDropboxTokenSource
{
    /// <summary>
    /// A valid access token. <paramref name="rejectedToken"/> is null on an ordinary call; after Dropbox answered
    /// 401 <c>expired_access_token</c> it is the token Dropbox refused, and the source returns a fresh one (or the
    /// token another caller has refreshed meanwhile). Returning the rejected token again ends the request with the
    /// 401 error.
    /// </summary>
    ValueTask<string> GetAccessTokenAsync(string? rejectedToken, CancellationToken ct = default);
}

/// <summary>A token source over an app's delegate: asked again, the same way, after a token expired.</summary>
internal sealed class DelegateTokenSource(Func<CancellationToken, ValueTask<string>> getAccessToken) : IDropboxTokenSource
{
    public ValueTask<string> GetAccessTokenAsync(string? rejectedToken, CancellationToken ct = default) =>
        getAccessToken(ct);
}

/// <summary>
/// Keeps the tokens of one Dropbox account fresh: refreshes the short-lived access token through
/// <see cref="DropboxAuth.RefreshAsync"/> a few minutes before it expires and when Dropbox refuses it. One refresh at
/// a time; <paramref name="onRefreshed"/> lets the app store the new tokens.
/// </summary>
public sealed class DropboxRefreshingTokenSource(
    DropboxAuth auth,
    DropboxTokens tokens,
    Func<DropboxTokens, CancellationToken, ValueTask>? onRefreshed = null) : IDropboxTokenSource
{
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DropboxTokens _tokens = tokens;

    public DropboxTokens Current => _tokens;

    /// <summary>For tests: the clock the expiry is checked against.</summary>
    internal Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    public async ValueTask<string> GetAccessTokenAsync(string? rejectedToken, CancellationToken ct = default)
    {
        var current = _tokens;

        if (!NeedsRefresh(current, rejectedToken))
            return current.AccessToken;

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            current = _tokens;

            if (!NeedsRefresh(current, rejectedToken))
                return current.AccessToken;

            if (current.RefreshToken is not { } refreshToken)
                throw new DropboxAuthException("no_refresh_token",
                    "The access token expired and there is no refresh token: sign in to Dropbox again.");

            var fresh = await auth.RefreshAsync(refreshToken, ct).ConfigureAwait(false);
            _tokens = fresh;

            if (onRefreshed is not null)
                await onRefreshed(fresh, ct).ConfigureAwait(false);

            return fresh.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    // A rejected token another caller has already replaced needs no second refresh
    private bool NeedsRefresh(DropboxTokens current, string? rejectedToken) =>
        rejectedToken is not null
            ? rejectedToken == current.AccessToken
            : current.ExpiresAt - ExpirySkew <= Now();
}
