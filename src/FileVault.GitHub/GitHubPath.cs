namespace FileVault.GitHub;

/// <summary>
/// <c>FullName</c> of GitHub items: <c>github:&lt;owner&gt;/&lt;repo&gt;@&lt;branch&gt;:/&lt;path&gt;</c>. Branch names may hold
/// slashes (<c>claude/fix</c>) but never a colon, so the first <c>:/</c> after the <c>@</c> ends the branch.
/// </summary>
public static class GitHubPath
{
    public const string Scheme = "github:";

    public static string Format(string owner, string name, string branch, string path) =>
        $"{Scheme}{owner}/{name}@{branch}:/{path}";

    public static bool TryParse(string fullName, out string owner, out string name, out string branch,
        out string path)
    {
        owner = name = branch = path = string.Empty;

        if (!fullName.StartsWith(Scheme, StringComparison.Ordinal))
            return false;

        var rest = fullName[Scheme.Length..];
        var at = rest.IndexOf('@');
        var slash = rest.IndexOf('/');

        if (slash <= 0 || at <= slash + 1)
            return false;

        var pathStart = rest.IndexOf(":/", at, StringComparison.Ordinal);

        if (pathStart <= at + 1)
            return false;

        owner = rest[..slash];
        name = rest[(slash + 1)..at];
        branch = rest[(at + 1)..pathStart];

        var relative = rest[(pathStart + 2)..].TrimEnd('/');

        if (relative.Length > 0 && !IsValidPath(relative))
            return false;

        path = relative;
        return true;
    }

    public static string Combine(string folder, string name) => folder.Length == 0 ? name : $"{folder}/{name}";

    public static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    /// <summary>
    /// A name for a new file or folder: one path segment that git can store and that cannot climb out of its folder
    /// or reach into <c>.git</c>.
    /// </summary>
    public static bool IsValidName(string name) =>
        name.Length is > 0 and <= 255
        && name is not "." and not ".."
        && !name.Equals(".git", StringComparison.OrdinalIgnoreCase)
        && name.IndexOfAny(['/', '\\']) < 0
        && !name.Any(char.IsControl);

    public static bool IsValidPath(string path) => path.Split('/').All(IsValidName);

    internal static string EnsureValidName(string name) =>
        IsValidName(name) ? name : throw new ArgumentException($"'{name}' cannot be a file or folder name.", nameof(name));
}
