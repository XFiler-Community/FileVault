namespace FileVault.OneDrive;

/// <summary>
/// Keeps one account's tokens fresh for <see cref="OneDriveClient"/>: refreshes the access token shortly before it
/// expires or when Graph refused it, one refresh at a time. Every refresh raises <see cref="TokensRefreshed"/> — store
/// the new refresh token there, since the old one may stop working.
/// </summary>
public sealed class OneDriveTokenSource
{
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(5);

    private readonly OneDriveAuth _auth;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OneDriveTokens _tokens;

    public OneDriveTokenSource(OneDriveAuth auth, OneDriveTokens tokens, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(tokens);

        _auth = auth;
        _tokens = tokens;
        _time = time ?? TimeProvider.System;
    }

    public OneDriveTokens Tokens => _tokens;

    /// <summary>Raised after every refresh with the new tokens (a rotated refresh token included).</summary>
    public event EventHandler<OneDriveTokens>? TokensRefreshed;

    /// <summary>Fits <see cref="OneDriveAccessTokenProvider"/>.</summary>
    public async ValueTask<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct = default)
    {
        var seen = _tokens;

        if (!forceRefresh && !IsExpiring(seen))
            return seen.AccessToken;

        OneDriveTokens refreshed;
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // Someone refreshed while this call waited: that token is the new one
            if (!ReferenceEquals(_tokens, seen) && !IsExpiring(_tokens))
                return _tokens.AccessToken;

            var refreshToken = _tokens.RefreshToken
                               ?? throw new OneDriveAuthException("no_refresh_token",
                                   "The access token expired and there is no refresh token: sign in again.");

            refreshed = _tokens = await _auth.RefreshAsync(refreshToken, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        TokensRefreshed?.Invoke(this, refreshed);
        return refreshed.AccessToken;
    }

    private bool IsExpiring(OneDriveTokens tokens) => tokens.ExpiresAt - ExpiryMargin <= _time.GetUtcNow();
}
