namespace FileVault.Core;

/// <summary>
/// Running totals for <see cref="IFolderSizeProvider"/>: bytes added per subfolder, snapshots taken while counting.
/// Thread-safe.
/// </summary>
public sealed class FolderSizeTally(IEqualityComparer<string>? comparer = null)
{
    private readonly Dictionary<string, long> _bytes = new(comparer ?? StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private bool _changed = true;

    /// <summary>Something was added since the last <see cref="Snapshot"/>.</summary>
    public bool HasChanged
    {
        get
        {
            lock (_lock)
                return _changed;
        }
    }

    /// <summary>Makes the subfolder known with 0 bytes, so an empty one is in the snapshots too.</summary>
    public void AddFolder(string name)
    {
        lock (_lock)
        {
            if (_bytes.TryAdd(name, 0))
                _changed = true;
        }
    }

    public void Add(string name, long bytes)
    {
        lock (_lock)
        {
            _bytes[name] = _bytes.GetValueOrDefault(name) + Math.Max(0, bytes);
            _changed = true;
        }
    }

    public FolderSizes Snapshot(bool isComplete)
    {
        lock (_lock)
        {
            _changed = false;
            return new FolderSizes(new Dictionary<string, long>(_bytes, _bytes.Comparer), isComplete);
        }
    }

    /// <summary>
    /// The subfolder of <paramref name="folder"/> that <paramref name="path"/> lies in: the first segment of the path
    /// after the folder, when it continues past that segment or is a folder itself. Null for a path outside the
    /// folder, or a file directly in it.
    /// </summary>
    /// <param name="folder">Folder path; a trailing separator is optional.</param>
    /// <param name="path">Path of an item below it, in the same form.</param>
    /// <param name="isFolder">The item at <paramref name="path"/> is a folder (a direct child then names itself).</param>
    public static string? SubfolderOf(string folder, string path, bool isFolder = false,
        StringComparison comparison = StringComparison.Ordinal, char separator = '/')
    {
        var prefix = folder.EndsWith(separator) ? folder : folder + separator;

        if (!path.StartsWith(prefix, comparison) || path.Length == prefix.Length)
            return null;

        var rest = path.AsSpan(prefix.Length);
        var end = rest.IndexOf(separator);

        if (end < 0)
            return isFolder ? rest.ToString() : null;

        return end == 0 ? null : rest[..end].ToString();
    }
}
