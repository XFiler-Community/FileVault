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
}
