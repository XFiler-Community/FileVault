namespace FileVault.Core;

public static class FileOperationsHelper
{
    private const int DefaultBufferSize = 1024 * 1024; // 1 MB

    /// <summary>
    /// Копирует данные из sourceStream в destStream с репортингом прогресса.
    /// Владение стримами НЕ передаётся — вызывающий диспозит сам.
    /// </summary>
    public static async Task CopyAsync(
        Stream sourceStream,
        long totalBytes,
        Stream destStream,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        Memory<byte> buffer = new byte[DefaultBufferSize];
        long written = 0;
        int read;

        while ((read = await sourceStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await destStream.WriteAsync(buffer[..read], ct).ConfigureAwait(false);
            written += read;

            if (totalBytes > 0)
                progress?.Report((double)written / totalBytes);
        }

        progress?.Report(1.0);
    }
}
