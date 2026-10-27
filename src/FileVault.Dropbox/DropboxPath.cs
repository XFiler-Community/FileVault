namespace FileVault.Dropbox;

/// <summary>
/// <c>FullName</c> of Dropbox items: <c>dropbox:/</c> for the root, <c>dropbox:/Photos/2024/a.jpg</c> below it —
/// the scheme followed by the Dropbox path with its display casing. Dropbox paths are case-insensitive, so two full
/// names that differ only in case name the same item (<see cref="AreSame"/>). The full name does not
/// say which account it belongs to: an app with several accounts keeps that next to it.
/// </summary>
public static class DropboxPath
{
    public const string Scheme = "dropbox:";

    /// <summary><paramref name="path"/> as Dropbox writes it: empty for the root, else starting with <c>/</c>.</summary>
    public static string Format(string path) => $"{Scheme}{(path.Length == 0 ? "/" : path)}";

    public static bool TryParse(string fullName, out string path)
    {
        path = string.Empty;

        if (!fullName.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = fullName[Scheme.Length..];

        if (!rest.StartsWith('/'))
            return false;

        var trimmed = rest.TrimEnd('/');

        if (trimmed.Length > 0 && !trimmed[1..].Split('/').All(IsValidName))
            return false;

        path = trimmed;
        return true;
    }

    /// <summary>A Dropbox path from either form: <c>dropbox:/a</c>, <c>/a</c>, <c>a</c>; empty is the root.</summary>
    public static string Normalize(string path)
    {
        if (TryParse(path, out var parsed))
            return parsed;

        var trimmed = path.Trim('/');
        return trimmed.Length == 0 ? string.Empty : "/" + trimmed;
    }

    public static string Combine(string folder, string name) => $"{folder}/{name}";

    public static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash <= 0 ? string.Empty : path[..slash];
    }

    public static string GetName(string path) => path[(path.LastIndexOf('/') + 1)..];

    public static bool AreSame(string path1, string path2) => string.Equals(path1, path2, StringComparison.OrdinalIgnoreCase);

    /// <summary><paramref name="path"/> is <paramref name="folder"/> itself or below it.</summary>
    public static bool IsInside(string path, string folder) =>
        folder.Length == 0
        || AreSame(path, folder)
        || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>One path segment Dropbox can store and that cannot climb out of its folder.</summary>
    public static bool IsValidName(string name) =>
        name.Length is > 0 and <= 255
        && name is not "." and not ".."
        && name.IndexOf('/') < 0
        && !name.Any(char.IsControl);

    internal static string EnsureValidName(string name) =>
        IsValidName(name) ? name : throw new ArgumentException($"'{name}' cannot be a file or folder name.", nameof(name));
}
