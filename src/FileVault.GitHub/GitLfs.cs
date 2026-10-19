using System.Text;
using System.Text.RegularExpressions;

namespace FileVault.GitHub;

/// <summary>
/// The small text file git keeps in place of a Git LFS object
/// (https://github.com/git-lfs/git-lfs/blob/main/docs/spec.md).
/// </summary>
public sealed record GitLfsPointer(string Oid, long Size)
{
    /// <summary>Pointers are under 200 bytes; anything this big is real content.</summary>
    public const int MaxPointerSize = 1024;

    private const string VersionLine = "version https://git-lfs.github.com/spec/v1";

    public static GitLfsPointer? TryParse(ReadOnlySpan<byte> content)
    {
        if (content.Length is 0 or > MaxPointerSize)
            return null;

        string text;

        try
        {
            text = new UTF8Encoding(false, true).GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lines.Length < 3 || lines[0] != VersionLine)
            return null;

        string? oid = null;
        long? size = null;

        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("oid sha256:", StringComparison.Ordinal))
                oid = line["oid sha256:".Length..];
            else if (line.StartsWith("size ", StringComparison.Ordinal) && long.TryParse(line[5..], out var parsed))
                size = parsed;
        }

        return oid is { Length: 64 } && size is >= 0 ? new GitLfsPointer(oid, size.Value) : null;
    }
}

/// <summary>
/// The <c>filter=lfs</c> patterns of a repository's root <c>.gitattributes</c>, matched the way git matches them:
/// a pattern without a slash matches the file name at any depth, one with a slash matches the path from the root,
/// <c>**</c> spans folders.
/// </summary>
public sealed class GitAttributes
{
    public static GitAttributes Empty { get; } = new([]);

    private readonly IReadOnlyList<(Regex Pattern, bool MatchesName, bool IsLfs)> _rules;

    private GitAttributes(IReadOnlyList<(Regex, bool, bool)> rules) => _rules = rules;

    public static GitAttributes Parse(string text)
    {
        var rules = new List<(Regex, bool, bool)>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var pattern = parts[0];

            bool? isLfs = null;

            foreach (var attribute in parts.Skip(1))
            {
                if (attribute == "filter=lfs")
                    isLfs = true;
                else if (attribute is "-filter" or "!filter" || attribute.StartsWith("filter=", StringComparison.Ordinal))
                    isLfs = false;
            }

            if (isLfs is null)
                continue;

            var matchesName = !pattern.TrimEnd('/').Contains('/');
            rules.Add((ToRegex(pattern.TrimStart('/')), matchesName, isLfs.Value));
        }

        return new GitAttributes(rules);
    }

    /// <param name="path">Relative to the repository root, with <c>/</c>.</param>
    public bool IsLfs(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var result = false;

        // The last matching line wins, as in git
        foreach (var (pattern, matchesName, isLfs) in _rules)
        {
            if (pattern.IsMatch(matchesName ? name : path))
                result = isLfs;
        }

        return result;
    }

    private static Regex ToRegex(string glob)
    {
        var regex = new StringBuilder("^");

        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];

            switch (c)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    // "**/" spans any number of folders, a trailing "**" everything below
                    var slash = i + 2 < glob.Length && glob[i + 2] == '/';
                    regex.Append(slash ? "(?:.*/)?" : ".*");
                    i += slash ? 2 : 1;
                    break;
                case '*':
                    regex.Append("[^/]*");
                    break;
                case '?':
                    regex.Append("[^/]");
                    break;
                case '[':
                    var close = glob.IndexOf(']', i + 1);

                    if (close < 0)
                    {
                        regex.Append(@"\[");
                        break;
                    }

                    var set = glob[(i + 1)..close].Replace(@"\", @"\\");
                    regex.Append('[').Append(set.StartsWith('!') ? "^" + set[1..] : set).Append(']');
                    i = close;
                    break;
                default:
                    regex.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        return new Regex(regex.Append('$').ToString(), RegexOptions.CultureInvariant);
    }
}
