namespace FileVault.Core;

/// <summary>
/// Optional: how many files this provider can transfer at once (CopyFileInAsync calls into it, or OpenReadAsync of
/// its items). An orchestrator copying many files runs up to the smaller value of the source and the target at a
/// time. A provider without this interface takes one file at a time.
/// </summary>
public interface IParallelTransferProvider
{
    /// <summary>
    /// Files at once, at least 1: 1 for a backend whose connection serves one request at a time (FTP) or whose writes
    /// must not overlap; more for HTTP backends, which limit each request rather than the account.
    /// </summary>
    int MaxParallelTransfers { get; }
}
