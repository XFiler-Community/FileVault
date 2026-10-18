namespace FileVault.Local;

// Names reach this provider from users and from other backends (Google Drive allows '/' in names),
// so a raw Path.Combine(root, name) could write outside the target folder.
internal static class LocalFileNames
{
    private static readonly char[] InvalidChars =
        [.. Path.GetInvalidFileNameChars().Union([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])];

    public static string Validate(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!IsValid(name))
            throw new ArgumentException($"'{name}' is not a valid file or folder name.", nameof(name));

        return name;
    }

    // Used for names coming from another backend: copying "2024/25.pdf" from the cloud must still work,
    // so invalid characters are replaced instead of failing the whole operation.
    public static string Sanitize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var sanitized = string.Create(name.Length, name, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                span[i] = Array.IndexOf(InvalidChars, source[i]) >= 0 ? '_' : source[i];
        });

        return IsValid(sanitized) ? sanitized : "_";
    }

    private static bool IsValid(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && name is not "." and not ".."
        && name.IndexOfAny(InvalidChars) < 0;
}
