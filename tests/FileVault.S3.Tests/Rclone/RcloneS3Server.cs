using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace FileVault.S3.Tests.Rclone;

/// <summary>
/// <c>rclone serve s3</c> on a temporary folder for every test in this namespace: each folder at the root is a bucket,
/// keys are files below it. Started once on a free port with test keys; tests are ignored when rclone is missing
/// (set <c>RCLONE_PATH</c> if it is not on PATH or in Homebrew's folders).
/// </summary>
[SetUpFixture]
public sealed class RcloneS3Server
{
    public const string Bucket = "bucket";
    private const string AccessKey = "FILEVAULTTESTKEY";
    private const string SecretKey = "filevault-test-secret/+key";

    private static Process? _process;
    private static int _port;

    /// <summary>The folder rclone serves; <c>&lt;Root&gt;/&lt;bucket&gt;/&lt;key&gt;</c> is an object.</summary>
    public static string Root { get; private set; } = "";

    public static string BucketFolder => Path.Combine(Root, Bucket);

    public static S3Connection Connect(long? multipartThreshold = null, long? partSize = null,
        long? multipartCopyThreshold = null)
    {
        var defaults = new S3Connection
            { Endpoint = new Uri("http://x"), Bucket = "x", AccessKeyId = "x", SecretAccessKey = "x" };

        return new S3Connection
        {
            Endpoint = new Uri($"http://127.0.0.1:{_port}"),
            Region = "us-east-1",
            Bucket = Bucket,
            AccessKeyId = AccessKey,
            SecretAccessKey = SecretKey,
            ForcePathStyle = true,
            MultipartThreshold = multipartThreshold ?? defaults.MultipartThreshold,
            PartSize = partSize ?? defaults.PartSize,
            MultipartCopyThreshold = multipartCopyThreshold ?? defaults.MultipartCopyThreshold
        };
    }

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        var rclone = FindRclone();

        if (rclone is null)
            Assert.Ignore("rclone is not installed; set RCLONE_PATH to run the S3 integration tests.");

        Root = Path.Combine(Path.GetTempPath(), $"filevault-s3-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(Root, Bucket));
        _port = FreePort();

        var start = new ProcessStartInfo(rclone)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        foreach (var argument in (string[])
                 [
                     "serve", "s3", Root,
                     "--addr", $"127.0.0.1:{_port}",
                     "--auth-key", $"{AccessKey},{SecretKey}",
                     // Files seeded straight into the folder must be listed at once
                     "--dir-cache-time", "0s",
                     "--config", "/dev/null",
                     "--log-level", "ERROR"
                 ])
            start.ArgumentList.Add(argument);

        _process = Process.Start(start)!;
        _process.OutputDataReceived += (_, e) => Log(e.Data);
        _process.ErrorDataReceived += (_, e) => Log(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (_process.HasExited)
                Assert.Fail($"rclone serve s3 exited with code {_process.ExitCode}.");

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, _port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100);
            }
        }

        Assert.Fail("rclone serve s3 did not start listening within 10 seconds.");
    }

    [OneTimeTearDown]
    public void Stop()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }

        _process?.Dispose();

        if (Root.Length > 0 && Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }

    private static void Log(string? line)
    {
        if (!string.IsNullOrEmpty(line))
            TestContext.Progress.WriteLine($"rclone: {line}");
    }

    private static string? FindRclone()
    {
        if (Environment.GetEnvironmentVariable("RCLONE_PATH") is { Length: > 0 } configured)
            return File.Exists(configured) ? configured : null;

        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"]);

        var name = OperatingSystem.IsWindows() ? "rclone.exe" : "rclone";
        return folders.Select(folder => Path.Combine(folder, name)).FirstOrDefault(File.Exists);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
