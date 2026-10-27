using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;

namespace FileVault.OneDrive.Tests;

[TestFixture]
public class OneDriveAuthTests
{
    private const string ClientId = "11111111-2222-3333-4444-555555555555";
    private static readonly Uri Redirect = new("http://localhost:53682/");

    private FakeTokenEndpoint _endpoint = null!;
    private OneDriveAuth _auth = null!;

    [SetUp]
    public void SetUp()
    {
        _endpoint = new FakeTokenEndpoint();
        _auth = new OneDriveAuth(new HttpClient(_endpoint), ClientId);
    }

    [TearDown]
    public void TearDown() => _endpoint.Dispose();

    [Test]
    public void AuthorizeUrl_IsCommonAuthorityWithPkceS256()
    {
        var request = _auth.CreateAuthorizationRequest(Redirect, loginHint: "anna@example.com");
        var query = HttpUtility.ParseQueryString(request.Url.Query);

        Assert.That(request.Url.GetLeftPart(UriPartial.Path),
            Is.EqualTo("https://login.microsoftonline.com/common/oauth2/v2.0/authorize"));
        Assert.That(query["client_id"], Is.EqualTo(ClientId));
        Assert.That(query["response_type"], Is.EqualTo("code"));
        Assert.That(query["response_mode"], Is.EqualTo("query"));
        Assert.That(query["redirect_uri"], Is.EqualTo("http://localhost:53682/"));
        Assert.That(query["scope"], Is.EqualTo("Files.ReadWrite offline_access User.Read"));
        Assert.That(query["state"], Is.EqualTo(request.State));
        Assert.That(query["code_challenge_method"], Is.EqualTo("S256"));
        Assert.That(query["prompt"], Is.EqualTo("select_account"));
        Assert.That(query["login_hint"], Is.EqualTo("anna@example.com"));
        Assert.That(query["client_secret"], Is.Null);

        var expectedChallenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(request.CodeVerifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.That(query["code_challenge"], Is.EqualTo(expectedChallenge));
        Assert.That(request.CodeVerifier, Has.Length.InRange(43, 128).And.Match("^[A-Za-z0-9._~-]+$"));
    }

    [Test]
    public void EveryRequest_HasFreshStateAndVerifier()
    {
        var a = _auth.CreateAuthorizationRequest(Redirect);
        var b = _auth.CreateAuthorizationRequest(Redirect);

        Assert.That(a.State, Is.Not.EqualTo(b.State));
        Assert.That(a.CodeVerifier, Is.Not.EqualTo(b.CodeVerifier));
    }

    // RFC 7636, appendix B
    [Test]
    public void CodeChallenge_MatchesRfc7636Example() =>
        Assert.That(OneDriveAuth.CodeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"),
            Is.EqualTo("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"));

    [Test]
    public void TenantAndScopes_AreConfigurable()
    {
        var auth = new OneDriveAuth(new HttpClient(_endpoint), ClientId, tenant: "consumers", scopes: "Files.Read offline_access");
        var query = HttpUtility.ParseQueryString(auth.CreateAuthorizationRequest(Redirect, prompt: null).Url.Query);

        Assert.That(auth.CreateAuthorizationRequest(Redirect).Url.AbsolutePath, Is.EqualTo("/consumers/oauth2/v2.0/authorize"));
        Assert.That(query["scope"], Is.EqualTo("Files.Read offline_access"));
        Assert.That(query["prompt"], Is.Null);
    }

    [Test]
    public void Callback_GivesCode_OrRefusesForeignStateAndErrors()
    {
        var request = _auth.CreateAuthorizationRequest(Redirect);

        var code = OneDriveAuth.GetAuthorizationCode(request,
            new Uri($"http://localhost:53682/?code=M.C507_abc&state={Uri.EscapeDataString(request.State)}"));
        var forged = Assert.Throws<OneDriveAuthException>(() => OneDriveAuth.GetAuthorizationCode(request,
            new Uri("http://localhost:53682/?code=M.C507_abc&state=other")));
        var denied = Assert.Throws<OneDriveAuthException>(() => OneDriveAuth.GetAuthorizationCode(request,
            new Uri($"http://localhost:53682/?error=access_denied&error_description=The+user+declined&state={request.State}")));

        Assert.That(code, Is.EqualTo("M.C507_abc"));
        Assert.That(forged!.Error, Is.EqualTo("state_mismatch"));
        Assert.That(denied!.Error, Is.EqualTo("access_denied"));
        Assert.That(denied.Description, Is.EqualTo("The user declined"));
    }

    [Test]
    public async Task RedeemCode_PostsVerifierWithoutSecret()
    {
        var request = _auth.CreateAuthorizationRequest(Redirect);

        var tokens = await _auth.RedeemCodeAsync(request, "M.C507_abc");

        var form = _endpoint.Forms.Single();
        Assert.That(_endpoint.Urls.Single(), Is.EqualTo("https://login.microsoftonline.com/common/oauth2/v2.0/token"));
        Assert.That(form["grant_type"], Is.EqualTo("authorization_code"));
        Assert.That(form["client_id"], Is.EqualTo(ClientId));
        Assert.That(form["code"], Is.EqualTo("M.C507_abc"));
        Assert.That(form["redirect_uri"], Is.EqualTo("http://localhost:53682/"));
        Assert.That(form["code_verifier"], Is.EqualTo(request.CodeVerifier));
        Assert.That(form["client_secret"], Is.Null);
        Assert.That(tokens.AccessToken, Is.EqualTo("access-1"));
        Assert.That(tokens.RefreshToken, Is.EqualTo("refresh-1"));
        Assert.That(tokens.ExpiresAt, Is.EqualTo(DateTimeOffset.UtcNow.AddHours(1)).Within(TimeSpan.FromMinutes(1)));
    }

    [Test]
    public async Task Refresh_ReturnsRotatedToken_OrKeepsTheOldOne()
    {
        var rotated = await _auth.RefreshAsync("refresh-0");
        _endpoint.OmitRefreshToken = true;
        var kept = await _auth.RefreshAsync("refresh-1");

        Assert.That(_endpoint.Forms[0]["grant_type"], Is.EqualTo("refresh_token"));
        Assert.That(_endpoint.Forms[0]["refresh_token"], Is.EqualTo("refresh-0"));
        Assert.That(_endpoint.Forms[0]["scope"], Is.EqualTo(OneDriveAuth.DefaultScopes));
        Assert.That(rotated.RefreshToken, Is.EqualTo("refresh-1"));
        Assert.That(kept.RefreshToken, Is.EqualTo("refresh-1"));
    }

    [Test]
    public void Refresh_Revoked_IsInvalidGrant()
    {
        _endpoint.Error = "invalid_grant";

        var e = Assert.ThrowsAsync<OneDriveAuthException>(() => _auth.RefreshAsync("refresh-0"));

        Assert.That(e!.Error, Is.EqualTo("invalid_grant"));
    }

    [Test]
    public async Task TokenSource_RefreshesWhenExpiring_AndOnDemand_RaisingEvent()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var source = new OneDriveTokenSource(_auth,
            new OneDriveTokens("access-0", "refresh-0", time.GetUtcNow().AddHours(1), null), time);
        var raised = new List<OneDriveTokens>();
        source.TokensRefreshed += (_, tokens) => raised.Add(tokens);

        var fresh = await source.GetAccessTokenAsync(false);
        time.Now = time.Now.AddMinutes(56);
        var renewed = await source.GetAccessTokenAsync(false);
        var forced = await source.GetAccessTokenAsync(true);

        Assert.That(fresh, Is.EqualTo("access-0"));
        Assert.That(renewed, Is.EqualTo("access-1"));
        Assert.That(forced, Is.EqualTo("access-2"));
        Assert.That(raised.Select(t => t.RefreshToken), Is.EqualTo(new[] { "refresh-1", "refresh-2" }));
        Assert.That(source.Tokens.RefreshToken, Is.EqualTo("refresh-2"));
    }

    [Test]
    public async Task TokenSource_ConcurrentCallers_ShareOneRefresh()
    {
        _endpoint.Latency = TimeSpan.FromMilliseconds(100);
        var source = new OneDriveTokenSource(_auth,
            new OneDriveTokens("access-0", "refresh-0", DateTimeOffset.UtcNow.AddMinutes(-1), null));

        var tokens = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => source.GetAccessTokenAsync(false).AsTask()));

        Assert.That(tokens, Is.All.EqualTo("access-1"));
        Assert.That(_endpoint.Forms, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task TokenSource_FeedsTheClient()
    {
        using var graph = new FakeGraph { AccessToken = "access-1" };
        var source = new OneDriveTokenSource(_auth,
            new OneDriveTokens("access-0", "refresh-0", DateTimeOffset.UtcNow.AddHours(1), null));
        var client = new OneDriveClient(new HttpClient(graph), source.GetAccessTokenAsync);

        var drive = await client.GetDriveAsync();

        Assert.That(drive.TotalSize, Is.GreaterThan(0), "the 401 on access-0 made the source refresh to access-1");
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>The identity platform's token endpoint: every grant gets the next access-N / refresh-N pair.</summary>
    private sealed class FakeTokenEndpoint : HttpMessageHandler
    {
        private int _issued;

        public List<string> Urls { get; } = [];
        public List<System.Collections.Specialized.NameValueCollection> Forms { get; } = [];
        public bool OmitRefreshToken { get; set; }
        public string? Error { get; set; }
        public TimeSpan Latency { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            Forms.Add(HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct)));
            await Task.Delay(Latency, ct);

            if (Error is { } error)
                return Answer(HttpStatusCode.BadRequest, new JsonObject
                {
                    ["error"] = error,
                    ["error_description"] = "AADSTS70000: The grant is expired or revoked."
                });

            var n = Interlocked.Increment(ref _issued);
            var json = new JsonObject
            {
                ["token_type"] = "Bearer",
                ["scope"] = "Files.ReadWrite User.Read",
                ["expires_in"] = 3600,
                ["access_token"] = $"access-{n}"
            };

            if (!OmitRefreshToken)
                json["refresh_token"] = $"refresh-{n}";

            return Answer(HttpStatusCode.OK, json);
        }

        private static HttpResponseMessage Answer(HttpStatusCode status, JsonObject json) => new(status)
        {
            Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json")
        };
    }
}
