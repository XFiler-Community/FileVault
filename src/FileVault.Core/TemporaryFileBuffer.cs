namespace FileVault.Core;

/// <summary>
/// Buffers a download in a temporary file instead of memory, so files of any size can be read without
/// running out of memory or hitting the 2 GB limit of <see cref="MemoryStream"/>. Needs free space in the
/// temp folder equal to the file size.
/// </summary>
public static class TemporaryFileBuffer
{
    /// <summary>
    /// Creates a temporary file, lets <paramref name="fill"/> write the content into it and returns it opened
    /// for reading from the start. The file is deleted when the returned stream is disposed, or right away
    /// if <paramref name="fill"/> throws.
    /// </summary>
    public static async Task<FileStream> CreateAsync(Func<Stream, CancellationToken, Task> fill,
        CancellationToken ct = default)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.DeleteOnClose | FileOptions.Asynchronous
        };

        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        var stream = new FileStream(Path.Combine(Path.GetTempPath(), $"filevault-{Guid.NewGuid():N}.tmp"), options);

        try
        {
            await fill(stream, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
