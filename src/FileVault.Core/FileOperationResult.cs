using System.Diagnostics.CodeAnalysis;

namespace FileVault.Core;

/// <summary>An operation finished and gave <paramref name="Value"/>.</summary>
public sealed record OperationSucceeded<T>(T Value);

/// <summary>An operation failed with <paramref name="Exception"/>; converts to any operation result.</summary>
public sealed record OperationFailed(Exception Exception)
{
    public string Message => Exception.Message;
}

/// <summary>
/// What an operation came to: a value or a failure, never both. Match it with an exhaustive
/// <c>switch</c> over <see cref="OperationSucceeded{T}"/> and <see cref="OperationFailed"/>, or read the members
/// below. It is a struct: <c>default</c> holds neither case and counts as not successful.
/// </summary>
public union FileOperationResult<T>(OperationSucceeded<T>, OperationFailed)
{
    public static FileOperationResult<T> Success(T result) => new OperationSucceeded<T>(result);

    public static FileOperationResult<T> Failure(Exception exception) => new OperationFailed(exception);

    public bool IsSuccess => this is OperationSucceeded<T>;

    /// <summary>The value; <c>default</c> after a failure.</summary>
    public T? Result => this is OperationSucceeded<T> succeeded ? succeeded.Value : default;

    public Exception? Exception => this is OperationFailed failed ? failed.Exception : null;

    public string? ErrorMessage => Exception?.Message;

    /// <summary>The same outcome with the result mapped; a failure keeps its exception.</summary>
    public FileOperationResult<TOther> Map<TOther>(Func<T, TOther> map) => this switch
    {
        OperationSucceeded<T> succeeded => FileOperationResult<TOther>.Success(map(succeeded.Value)),
        OperationFailed failed => failed,
        null => default
    };

    public bool TryGetResult([NotNullWhen(true)] out T? result)
    {
        result = Result;
        return IsSuccess;
    }
}

/// <summary>A provider copied or moved a folder itself; it now is <paramref name="Folder"/>.</summary>
public sealed record FolderOperationDone(IFolderItem Folder);

/// <summary>A provider cannot do this in one step (another provider's folder): the caller walks the tree.</summary>
public sealed record FolderOperationNotHandled
{
    internal static FolderOperationNotHandled Instance { get; } = new();
}

/// <summary>What a folder copy or move came to: done, left to the caller, or failed.</summary>
public union FolderOperationResult(FolderOperationDone, FolderOperationNotHandled, OperationFailed)
{
    public static FolderOperationResult Done(IFolderItem folder) => new FolderOperationDone(folder);

    public static FolderOperationResult NotHandled => FolderOperationNotHandled.Instance;

    public static FolderOperationResult Failure(Exception exception) => new OperationFailed(exception);

    /// <summary>Nothing went wrong: the folder is done, or it is left to the caller.</summary>
    public bool IsSuccess => this is FolderOperationDone or FolderOperationNotHandled;

    /// <summary>The folder when the provider did the work itself.</summary>
    public IFolderItem? Folder => this is FolderOperationDone done ? done.Folder : null;

    public Exception? Exception => this is OperationFailed failed ? failed.Exception : null;

    public string? ErrorMessage => Exception?.Message;
}
