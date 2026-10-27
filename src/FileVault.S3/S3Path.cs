namespace FileVault.S3;

/// <summary>
/// Paths inside a bucket: keys joined by <c>/</c>, a folder written without its trailing slash, the root empty.
/// The folder's key prefix — what S3 lists by — is the path plus <c>/</c>.
/// </summary>
internal static class S3Path
{
    public static string Normalize(string path) => path.Trim('/');

    public static string Combine(string folder, string name) => folder.Length == 0 ? name : $"{folder}/{name}";

    public static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    public static string GetName(string path) => path[(path.LastIndexOf('/') + 1)..];

    public static string Prefix(string folder) => folder.Length == 0 ? "" : folder + "/";

    public static void EnsureValidName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (name.Contains('/') || name is "." or "..")
            throw new ArgumentException($"'{name}' is not a valid name: no '/', '.' or '..'.", nameof(name));
    }

    public static void EnsureNotInside(string source, string destination)
    {
        if (source.Length == 0 || destination.StartsWith(source + "/", StringComparison.Ordinal))
            throw new IOException($"Cannot put '{source}' inside itself.");
    }
}
