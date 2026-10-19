namespace FileVault.GitHub;

/// <summary>Which repository and branch to work with, and how to reach GitHub.</summary>
public sealed class GitHubRepositoryOptions
{
    public required string Owner { get; init; }
    public required string Name { get; init; }
    public required string Branch { get; init; }

    /// <summary>An OAuth or GitHub App user token; null reads public repositories only (60 requests an hour).</summary>
    public string? Token { get; init; }

    public Uri ApiBaseUri { get; init; } = new("https://api.github.com/");

    /// <summary>Where Git LFS lives: <c>&lt;web&gt;/&lt;owner&gt;/&lt;repo&gt;.git/info/lfs</c>.</summary>
    public Uri WebBaseUri { get; init; } = new("https://github.com/");

    /// <summary>
    /// How long a read of the branch head is reused before listing asks GitHub again. Writes always start from
    /// a fresh head.
    /// </summary>
    public TimeSpan HeadCacheDuration { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>A commit of the branch, the state every listing and read of one moment is taken from.</summary>
public sealed record GitHubSnapshot(string CommitSha, string TreeSha, DateTimeOffset Date);

public enum GitHubEntryKind
{
    File,
    Folder
}

/// <summary>
/// A file or folder of a tree. <see cref="Path"/> is relative to the repository root with <c>/</c>.
/// For a Git LFS file <see cref="Size"/> is the object's size, not the pointer's.
/// </summary>
public sealed record GitHubEntry(string Path, GitHubEntryKind Kind, string Sha, string Mode, long Size, bool IsLfs)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
}

public enum GitHubWriteStatus
{
    Written,

    /// <summary>The file is not what the caller opened any more: nothing was written.</summary>
    ChangedMeanwhile,

    /// <summary>The path is tracked by Git LFS, which FileVault does not write.</summary>
    StoredInLfs
}

/// <param name="CommitSha">The new commit when <see cref="Status"/> is <see cref="GitHubWriteStatus.Written"/>.</param>
/// <param name="BlobSha">The file's blob now: the written one, or what is there instead when it changed meanwhile.</param>
public sealed record GitHubWriteResult(GitHubWriteStatus Status, string? CommitSha, string? BlobSha);

/// <summary>The branch kept moving under every attempt to commit on top of it.</summary>
public sealed class GitHubConcurrencyException(string message) : IOException(message);

public sealed record GitHubRepositoryInfo(string DefaultBranch, bool IsPrivate);

public sealed record GitHubBranch(string Name, string Sha);

/// <param name="AheadBy">Commits the head has that the base does not.</param>
/// <param name="BehindBy">Commits the base has that the head does not.</param>
public sealed record GitHubComparison(string Status, int AheadBy, int BehindBy);

public enum GitHubFileStatus
{
    Added,
    Modified,
    Removed,
    Renamed,
    Copied,
    Changed,
    Unchanged
}

/// <param name="PreviousPath">The old path of a renamed file.</param>
public sealed record GitHubCommitFile(string Path, GitHubFileStatus Status, string? PreviousPath);

/// <param name="AuthorLogin">The GitHub account of the author, when GitHub could match one.</param>
/// <param name="Files">Null in a list of commits; the files of a single commit (the first 300) otherwise.</param>
public sealed record GitHubCommit(
    string Sha,
    IReadOnlyList<string> ParentShas,
    string Message,
    string? AuthorName,
    string? AuthorEmail,
    string? AuthorLogin,
    DateTimeOffset AuthorDate,
    DateTimeOffset CommitterDate,
    IReadOnlyList<GitHubCommitFile>? Files);
