using System.Diagnostics.CodeAnalysis;

namespace FileVault.Core;

public sealed class FileOperationResult<T>
{
    public T? Result { get; init; }
    public Exception? Exception { get; init; }

    public bool IsSuccess => Exception is null;
    public string? ErrorMessage => Exception?.Message;

    public static FileOperationResult<T> Success(T result) => new() { Result = result };
    public static FileOperationResult<T> Failure(Exception ex) => new() { Exception = ex };

    /// <summary>The same outcome with the result mapped; a failure keeps its exception.</summary>
    public FileOperationResult<TOther> Map<TOther>(Func<T, TOther> map) =>
        IsSuccess ? FileOperationResult<TOther>.Success(map(Result!)) : FileOperationResult<TOther>.Failure(Exception!);

    public bool TryGetResult([NotNullWhen(true)] out T? result)
    {
        result = Result;
        return IsSuccess;
    }
}
