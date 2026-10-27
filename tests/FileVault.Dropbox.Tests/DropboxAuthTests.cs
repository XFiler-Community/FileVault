using System.Security.Cryptography;
using System.Text;

namespace FileVault.Dropbox.Tests;

[TestFixture]
public class DropboxAuthTests
{
    private FakeDropbox _dropbox = null!;
    private DropboxAuth _auth = null!;
    private readonly DateTimeOffset _now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [SetUp]
    public void SetUp()
    {
        _dropbox = new FakeDropbox();
        _auth = new DropboxAuth(FakeDropbox.AppKey, new HttpClient(_dropbox)) { Now = () => _now };
    }

    [TearDown]
    public void TearDown() => _dropbox.Dispose();

    [Test]
    public void AuthorizeUri_PasteFlow_HasPkceAndOfflineAccess_NoRedirect()
    {
        var request = _auth.CreateAuthorizationRequest();
        var query = Query(request.AuthorizeUri);

        Assert.That(request.AuthorizeUri.GetLeftPart(UriPartial.Path), Is.EqualTo("https://www.dropbox.com/oauth2/authorize"));
        Assert.That(query["client_id"], Is.EqualTo(FakeDropbox.AppKey));
        Assert.That(query["response_type"], Is.EqualTo("code"));
        Assert.That(query["token_access_type"], Is.EqualTo("offline"));
        Assert.That(query["code_challenge_method"], Is.EqualTo("S256"));
        Assert.That(query["code_challenge"], Is.EqualTo(Challenge(request.CodeVerifier)));
        Assert.That(query["state"], Is.EqualTo(request.State));
        Assert.That(query.ContainsKey("redirect_uri"), Is.False);
        Assert.That(query.ContainsKey("client_secret"), Is.False);
    }

    [Test]
    public void AuthorizeUri_LoopbackFlow_CarriesRedirectAndScopes()
    {
        var redirect = new Uri("http://127.0.0.1:53682/dropbox");
        var request = _auth.CreateAuthorizationRequest(redirect, ["files.content.read", "files.content.write"],
            forceReapprove: true);
        var query = Query(request.AuthorizeUri);

        Assert.That(query["redirect_uri"], Is.EqualTo("http://127.0.0.1:53682/dropbox"));
        Assert.That(query["scope"], Is.EqualTo("files.content.read files.content.write"));
        Assert.That(query["force_reapprove"], Is.EqualTo("true"));
        Assert.That(request.RedirectUri, Is.EqualTo(redirect));
    }

    [Test]
    public void CodeVerifier_FollowsPkceRules_AndIsFreshEachTime()
    {
        var first = _auth.CreateAuthorizationRequest();
        var second = _auth.CreateAuthorizationRequest();

        Assert.That(first.CodeVerifier.Length, Is.InRange(43, 128));
        Assert.That(first.CodeVerifier.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~'), Is.True);
        Assert.That(second.CodeVerifier, Is.Not.EqualTo(first.CodeVerifier));
        Assert.That(second.State, Is.Not.EqualTo(first.State));
    }

    [Test]
    public void CodeChallenge_MatchesRfc7636Example()
    {
        // RFC 7636, appendix B
        Assert.That(DropboxAuth.CreateCodeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"),
            Is.EqualTo("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"));
    }

    [Test]
    public async Task PastedCode_IsExchangedWithVerifier_WithoutRedirect()
    {
        var request = _auth.CreateAuthorizationRequest();
        _dropbox.ExpectCode("pasted-code", Challenge(request.CodeVerifier), redirectUri: null);

        var tokens = await _auth.ExchangeCodeAsync(request, "  pasted-code \n");

        Assert.That(tokens.AccessToken, Does.StartWith("sl.access-"));
        Assert.That(tokens.RefreshToken, Does.StartWith("refresh-"));
        Assert.That(tokens.ExpiresAt, Is.EqualTo(_now.AddSeconds(14400)));
        Assert.That(tokens.AccountId, Is.EqualTo("dbid:fake"));
        Assert.That(_dropbox.LastTokenForm!["grant_type"], Is.EqualTo("authorization_code"));
        Assert.That(_dropbox.LastTokenForm.ContainsKey("redirect_uri"), Is.False);
        Assert.That(_dropbox.LastTokenForm.ContainsKey("client_secret"), Is.False);
    }

    [Test]
    public async Task LoopbackRedirect_IsParsed_AndExchangedWithTheSameRedirectUri()
    {
        var redirect = new Uri("http://127.0.0.1:53682/dropbox");
        var request = _auth.CreateAuthorizationRequest(redirect);
        _dropbox.ExpectCode("abc", Challenge(request.CodeVerifier), redirect.OriginalString);

        var code = DropboxAuth.ParseRedirect(request, new Uri($"{redirect}?code=abc&state={Uri.EscapeDataString(request.State)}"));
        var tokens = await _auth.ExchangeCodeAsync(request, code);

        Assert.That(code, Is.EqualTo("abc"));
        Assert.That(tokens.RefreshToken, Is.Not.Null);
        Assert.That(_dropbox.LastTokenForm!["redirect_uri"], Is.EqualTo(redirect.OriginalString));
    }

    [Test]
    public void Redirect_WithForeignState_OrDenied_Throws()
    {
        var request = _auth.CreateAuthorizationRequest(new Uri("http://127.0.0.1:53682/"));

        var forged = Assert.Throws<DropboxAuthException>(() =>
            DropboxAuth.ParseRedirect(request, new Uri("http://127.0.0.1:53682/?code=x&state=other")));
        var denied = Assert.Throws<DropboxAuthException>(() => DropboxAuth.ParseRedirect(request,
            new Uri($"http://127.0.0.1:53682/?error=access_denied&error_description=The+user+chose+not+to+give+your+app+access&state={request.State}")));

        Assert.That(forged!.Error, Is.EqualTo("invalid_state"));
        Assert.That(denied!.Error, Is.EqualTo("access_denied"));
        Assert.That(denied.Description, Is.EqualTo("The user chose not to give your app access"));
    }

    [Test]
    public void WrongVerifier_IsRefusedByDropbox()
    {
        var request = _auth.CreateAuthorizationRequest();
        _dropbox.ExpectCode("code", Challenge("someone-elses-verifier-someone-elses-verifier"), null);

        var error = Assert.ThrowsAsync<DropboxAuthException>(() => _auth.ExchangeCodeAsync(request, "code"));

        Assert.That(error!.Error, Is.EqualTo("invalid_grant"));
    }

    [Test]
    public async Task Refresh_SendsClientIdOnly_AndKeepsTheRefreshToken()
    {
        _dropbox.AddRefreshToken("refresh-1");

        var tokens = await _auth.RefreshAsync("refresh-1");

        Assert.That(tokens.AccessToken, Does.StartWith("sl.refreshed-"));
        Assert.That(tokens.RefreshToken, Is.EqualTo("refresh-1"));
        Assert.That(_dropbox.LastTokenForm, Is.EquivalentTo(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = "refresh-1",
            ["client_id"] = FakeDropbox.AppKey
        }));
    }

    [Test]
    public async Task RefreshingTokenSource_RefreshesBeforeExpiry_AndAfterRejection_Once()
    {
        _dropbox.AddRefreshToken("refresh-1");
        var stored = new List<DropboxTokens>();
        var clock = _now;
        var source = new DropboxRefreshingTokenSource(_auth,
            new DropboxTokens("sl.first", "refresh-1", _now.AddHours(4), "dbid:fake", null),
            (tokens, _) => { stored.Add(tokens); return ValueTask.CompletedTask; }) { Now = () => clock };

        Assert.That(await source.GetAccessTokenAsync(null), Is.EqualTo("sl.first"));

        var afterRejection = await source.GetAccessTokenAsync("sl.first");
        var staleRejection = await source.GetAccessTokenAsync("sl.first");
        Assert.That(afterRejection, Does.StartWith("sl.refreshed-"));
        Assert.That(staleRejection, Is.EqualTo(afterRejection), "a token already replaced is not refreshed again");

        clock = _now.AddHours(4).AddMinutes(-1);
        var nearExpiry = await source.GetAccessTokenAsync(null);
        Assert.That(nearExpiry, Is.Not.EqualTo(afterRejection));
        Assert.That(stored, Has.Count.EqualTo(2));
        Assert.That(source.Current.RefreshToken, Is.EqualTo("refresh-1"));
    }

    [Test]
    public async Task ClientWithRefreshingSource_RecoversFromExpiredToken()
    {
        _dropbox.AddRefreshToken("refresh-1");
        _dropbox.ValidTokens.Add("sl.first");
        _dropbox.ExpiredTokens.Add("sl.first");
        var source = new DropboxRefreshingTokenSource(_auth,
            new DropboxTokens("sl.first", "refresh-1", _now.AddHours(4), null, null)) { Now = () => _now };
        var client = new DropboxClient(source, new HttpClient(_dropbox));

        var usage = await client.GetSpaceUsageAsync();

        Assert.That(usage.Allocated, Is.EqualTo(_dropbox.Quota));
        Assert.That(_dropbox.Routes, Is.EqualTo(new[] { "users/get_space_usage", "oauth2/token", "users/get_space_usage" }));
    }

    [Test]
    public async Task Revoke_SignsOut()
    {
        var client = _dropbox.CreateClient();

        await _auth.RevokeAsync("test-token");

        Assert.ThrowsAsync<DropboxApiException>(() => client.GetSpaceUsageAsync());
    }

    private static string Challenge(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
}
