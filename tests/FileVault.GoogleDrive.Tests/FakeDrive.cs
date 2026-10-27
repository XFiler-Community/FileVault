using System.Net;
using System.Text;
using Google.Apis.Drive.v3;
using Google.Apis.Http;
using Google.Apis.Services;

namespace FileVault.GoogleDrive.Tests;

/// <summary>A DriveService whose HTTP requests are answered by a function, so tests need no credentials.</summary>
internal static class FakeDrive
{
    public static DriveService CreateService(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(new BaseClientService.Initializer
        {
            HttpClientFactory = new FakeHttpClientFactory(new FakeHandler(respond)),
            ApplicationName = "FileVault.Tests",
            GZipEnabled = false
        });

    public static HttpResponseMessage Json(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) => handler;
    }
}
