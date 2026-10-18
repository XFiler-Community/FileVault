using System.Runtime.ExceptionServices;
using Google.Apis.Download;
using Google.Apis.Upload;

namespace FileVault.GoogleDrive;

// Google.Apis reports a failed media transfer through Status = Failed instead of throwing,
// so every UploadAsync/DownloadAsync result has to go through these checks.
internal static class GoogleDriveTransfer
{
    public static void EnsureCompleted(IUploadProgress progress)
    {
        if (progress.Status == UploadStatus.Completed)
            return;

        Rethrow(progress.Exception, $"Google Drive upload did not complete (status: {progress.Status}).");
    }

    public static void EnsureCompleted(IDownloadProgress progress)
    {
        if (progress.Status == DownloadStatus.Completed)
            return;

        Rethrow(progress.Exception, $"Google Drive download did not complete (status: {progress.Status}).");
    }

    private static void Rethrow(Exception? exception, string fallbackMessage)
    {
        if (exception is not null)
            ExceptionDispatchInfo.Throw(exception);

        throw new IOException(fallbackMessage);
    }
}
