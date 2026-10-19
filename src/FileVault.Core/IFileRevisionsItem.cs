namespace FileVault.Core;

/// <summary>
/// A past state of a file that its provider keeps. <see cref="Md5"/> is the checksum of that content, when the
/// provider gives one; <see cref="ModifiedBy"/> is the account that saved it, as the provider names it.
/// </summary>
public sealed record FileRevision(string Id, DateTimeOffset ModifiedDate, long? Size, string? Md5, string? ModifiedBy);

/// <summary>
/// Optional: a file whose provider keeps its past states (Google Drive revisions). Check with
/// <c>item is IFileRevisionsItem revisions</c>; providers without such history do not implement it.
/// </summary>
public interface IFileRevisionsItem : IFileItem
{
    /// <summary>Oldest first; the last one is the current content. Empty when the past states cannot be read.</summary>
    Task<IReadOnlyList<FileRevision>> GetRevisionsAsync(CancellationToken ct = default);

    /// <summary>The content of one revision. The caller owns the stream.</summary>
    Task<(Stream stream, long totalBytes)> OpenRevisionAsync(FileRevision revision, CancellationToken ct = default);
}
