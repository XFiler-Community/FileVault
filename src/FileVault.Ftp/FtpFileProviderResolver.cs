using FileVault.Core;
using FluentFTP;

namespace FileVault.Ftp;

public sealed class FtpConnection
{
    public required string Host { get; init; }
    public int Port { get; init; } = 21;
    public string? Username { get; init; }
    public string? Password { get; init; }
}

public sealed class FtpFileProviderResolver(FtpConnection connection) : IFileProviderResolver
{
    private AsyncFtpClient? _client;

    private async Task<AsyncFtpClient> GetClientAsync(CancellationToken ct)
    {
        if (_client is { IsConnected: true })
            return _client;

        _client = new AsyncFtpClient(connection.Host, connection.Username, connection.Password, connection.Port);
        await _client.Connect(ct).ConfigureAwait(false);
        return _client;
    }

    public async Task<IFileProvider?> ResolveAsync(string route, CancellationToken ct = default)
    {
        if (!route.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase))
            return null;

        var uri = new Uri(route);
        if (!string.Equals(uri.Host, connection.Host, StringComparison.OrdinalIgnoreCase))
            return null;

        var client = await GetClientAsync(ct).ConfigureAwait(false);
        return new FtpFileProvider(client, uri.AbsolutePath);
    }

    public Task<IReadOnlyList<IDriveItem>> GetDrivesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<IDriveItem>>([]);

    public Task<IFolderItem?> GetFolderAsync(string route, CancellationToken ct = default)
        => Task.FromResult<IFolderItem?>(null);
}
