using FileVault.Core;
using Renci.SshNet;

namespace FileVault.Sftp;

public sealed class SftpConnection
{
    public required string Host { get; init; }
    public int Port { get; init; } = 22;
    public required string Username { get; init; }
    public string? Password { get; init; }
    public string? PrivateKeyPath { get; init; }
}

public sealed class SftpFileProviderResolver(SftpConnection connection) : IFileProviderResolver
{
    private SftpClient? _client;

    private SftpClient GetClient()
    {
        if (_client is { IsConnected: true })
            return _client;

        ConnectionInfo info;
        if (connection.PrivateKeyPath is not null)
        {
            var key = new PrivateKeyFile(connection.PrivateKeyPath);
            info = new ConnectionInfo(connection.Host, connection.Port, connection.Username,
                new PrivateKeyAuthenticationMethod(connection.Username, key));
        }
        else
        {
            info = new ConnectionInfo(connection.Host, connection.Port, connection.Username,
                new PasswordAuthenticationMethod(connection.Username, connection.Password));
        }

        _client = new SftpClient(info);
        _client.Connect();
        return _client;
    }

    public Task<IFileProvider?> ResolveAsync(string route, CancellationToken ct = default)
    {
        if (!route.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<IFileProvider?>(null);

        var uri = new Uri(route);
        if (!string.Equals(uri.Host, connection.Host, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<IFileProvider?>(null);

        var client = GetClient();
        return Task.FromResult<IFileProvider?>(new SftpFileProvider(client, uri.AbsolutePath));
    }

    public Task<IReadOnlyList<IDriveItem>> GetDrivesAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<IDriveItem>>([]);

    public Task<IFolderItem?> GetFolderAsync(string route, CancellationToken ct = default)
        => Task.FromResult<IFolderItem?>(null);
}
