using System.Diagnostics;
using System.Text;
using FileVault.Core;
using FileVault.Local;

namespace FileVault.Wsl;

/// <summary>
/// Resolves wsl://&lt;distro&gt;/path routes to WSL filesystem via UNC paths (\\wsl$\).
/// Only functional on Windows 10 1903+ with WSL installed.
/// </summary>
public sealed class WslFileProviderResolver : IFileProviderResolver
{
    private const string UncRoot = @"\\wsl$\";
    private const string RoutePrefix = "wsl://";

    public Task<IFileProvider?> ResolveAsync(string route, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult<IFileProvider?>(null);

        if (!route.StartsWith(RoutePrefix, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<IFileProvider?>(null);

        var uncPath = RouteToUncPath(route);
        if (uncPath is null || !Directory.Exists(uncPath))
            return Task.FromResult<IFileProvider?>(null);

        return Task.FromResult<IFileProvider?>(new LocalFileProvider(uncPath));
    }

    public async Task<IReadOnlyList<IDriveItem>> GetDrivesAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return [];

        var distros = await GetInstalledDistrosAsync(ct).ConfigureAwait(false);
        return distros.Select(name => (IDriveItem)new WslDriveItem(name)).ToList();
    }

    public Task<IFolderItem?> GetFolderAsync(string route, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult<IFolderItem?>(null);

        if (!route.StartsWith(RoutePrefix, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<IFolderItem?>(null);

        var uncPath = RouteToUncPath(route);
        if (uncPath is null || !Directory.Exists(uncPath))
            return Task.FromResult<IFolderItem?>(null);

        return Task.FromResult<IFolderItem?>(new SystemFolderItem(new DirectoryInfo(uncPath)));
    }

    private static string? RouteToUncPath(string route)
    {
        // "wsl://Ubuntu"          → \\wsl$\Ubuntu\
        // "wsl://Ubuntu/home/bob" → \\wsl$\Ubuntu\home\bob
        var remainder = route[RoutePrefix.Length..];
        if (string.IsNullOrWhiteSpace(remainder))
            return null;

        var slashIdx = remainder.IndexOf('/');

        string distro, linuxPath;
        if (slashIdx < 0)
        {
            distro = remainder;
            linuxPath = @"\";
        }
        else
        {
            distro = remainder[..slashIdx];
            linuxPath = remainder[slashIdx..].Replace('/', '\\');
        }

        if (string.IsNullOrWhiteSpace(distro))
            return null;

        return UncRoot + distro + linuxPath;
    }

    private static async Task<IEnumerable<string>> GetInstalledDistrosAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                Arguments = "--list --quiet",
                RedirectStandardOutput = true,
                // wsl.exe outputs UTF-16 LE on Windows
                StandardOutputEncoding = Encoding.Unicode,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
                return [];

            var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            return output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim('\0', ' '))
                .Where(s => !string.IsNullOrWhiteSpace(s));
        }
        catch
        {
            return [];
        }
    }
}
