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

    public bool TryGetResult([NotNullWhen(true)] out T? result)
    {
        result = Result;
        return IsSuccess;
    }
}
