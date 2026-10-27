using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FileVault.WebDav.Tests;

/// <summary>
/// <c>rclone serve webdav</c> on a temp folder and a free port, with Basic auth and a base path, so the provider
/// meets a real server. rclone is looked up in <c>RCLONE_PATH</c>, Homebrew and PATH; without it tests are ignored.
/// </summary>
internal sealed class RcloneWebDavServer : IAsyncDisposable
{
    public const string UserName = "filevault";
    public const string Password = "pa ss:wörd";
    public const string BasePath = "/dav";

    private readonly Process _process;
    private readonly StringBuilder _log;

    private RcloneWebDavServer(Process process, StringBuilder log, string rootDirectory, Uri baseUri)
    {
        _process = process;
        _log = log;
        RootDirectory = rootDirectory;
        BaseUri = baseUri;
    }

    /// <summary>The local folder the server serves.</summary>
    public string RootDirectory { get; }

    public Uri BaseUri { get; }

    public WebDavConnection CreateConnection(string? password = Password) => new()
    {
        BaseUri = BaseUri,
        UserName = UserName,
        Password = password
    };

    public static async Task<RcloneWebDavServer> StartOrIgnoreAsync()
    {
        var rclone = FindRclone();

        if (rclone is null)
            Assert.Ignore("rclone is not installed (set RCLONE_PATH); WebDAV integration tests skipped.");

        var root = Path.Combine(Path.GetTempPath(), $"filevault-webdav-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var port = GetFreePort();

        var start = new ProcessStartInfo(rclone!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in new[]
                 {
                     "serve", "webdav", root,
                     "--addr", $"127.0.0.1:{port}",
                     "--baseurl", BasePath,
                     "--user", UserName,
                     "--pass", Password,
                     // Tests seed and check files on disk directly: the server must not answer from a cache.
                     "--dir-cache-time", "0s",
                     "--config", "/dev/null"
                 })
            start.ArgumentList.Add(argument);

        var log = new StringBuilder();
        var process = Process.Start(start)!;
        process.OutputDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { lock (log) log.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var server = new RcloneWebDavServer(process, log, root, new Uri($"http://127.0.0.1:{port}{BasePath}/"));
        await server.WaitUntilReadyAsync();
        return server;
    }

    private async Task WaitUntilReadyAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
                throw new InvalidOperationException($"rclone exited with {_process.ExitCode}:\n{_log}");

            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Loopback, BaseUri.Port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100);
            }
        }

        throw new TimeoutException($"rclone did not start listening on {BaseUri}:\n{_log}");
    }

    private static string? FindRclone()
    {
        if (Environment.GetEnvironmentVariable("RCLONE_PATH") is { Length: > 0 } configured && File.Exists(configured))
            return configured;

        var executable = OperatingSystem.IsWindows() ? "rclone.exe" : "rclone";
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"]);

        return directories.Select(d => Path.Combine(d, executable)).FirstOrDefault(File.Exists);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
        finally
        {
            _process.Dispose();

            try
            {
                Directory.Delete(RootDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
