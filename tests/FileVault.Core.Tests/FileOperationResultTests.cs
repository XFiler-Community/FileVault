using FileVault.Core;

namespace FileVault.Core.Tests;

[TestFixture]
public class FileOperationResultTests
{
    [Test]
    public void Success_IsSuccess_True()
    {
        var result = FileOperationResult<int>.Success(42);
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Result, Is.EqualTo(42));
        Assert.That(result.Exception, Is.Null);
        Assert.That(result.ErrorMessage, Is.Null);
    }

    [Test]
    public void Failure_IsSuccess_False()
    {
        var ex = new IOException("disk error");
        var result = FileOperationResult<int>.Failure(ex);
        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Exception, Is.SameAs(ex));
        Assert.That(result.ErrorMessage, Is.EqualTo("disk error"));
    }

    [Test]
    public void TryGetResult_Success_ReturnsTrue()
    {
        var result = FileOperationResult<string>.Success("hello");
        Assert.That(result.TryGetResult(out var val), Is.True);
        Assert.That(val, Is.EqualTo("hello"));
    }

    [Test]
    public void TryGetResult_Failure_ReturnsFalse()
    {
        var result = FileOperationResult<string>.Failure(new Exception());
        Assert.That(result.TryGetResult(out _), Is.False);
    }

    [Test]
    public void Success_NullableResult_AllowsNull()
    {
        var result = FileOperationResult<string?>.Success(null);
        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Result, Is.Null);
    }

    [Test]
    public void Cases_MatchExhaustively()
    {
        var error = new IOException("disk error");

        Assert.That(Describe(FileOperationResult<int>.Success(42)), Is.EqualTo("42"));
        Assert.That(Describe(new OperationFailed(error)), Is.EqualTo("disk error"));

        static string Describe(FileOperationResult<int> result) => result switch
        {
            OperationSucceeded<int> succeeded => succeeded.Value.ToString(),
            OperationFailed failed => failed.Message,
        };
    }

    [Test]
    public void Default_HoldsNeitherCase_AndIsNotSuccess()
    {
        FileOperationResult<int> result = default;

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Exception, Is.Null);
        Assert.That(result.Map(value => value + 1).IsSuccess, Is.False);
    }

    [Test]
    public void Folder_NotHandled_IsNoError_AndHasNoFolder()
    {
        var result = FolderOperationResult.NotHandled;

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result is FolderOperationNotHandled, Is.True);
        Assert.That(result.Folder, Is.Null);
        Assert.That(result.Exception, Is.Null);
    }

    [Test]
    public void Folder_Failure_KeepsItsException()
    {
        var error = new IOException("disk error");
        var result = FolderOperationResult.Failure(error);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Exception, Is.SameAs(error));
        Assert.That(result.ErrorMessage, Is.EqualTo("disk error"));
    }
}
