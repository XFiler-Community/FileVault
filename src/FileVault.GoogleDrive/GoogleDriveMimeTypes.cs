namespace FileVault.GoogleDrive;

internal static class GoogleDriveMimeTypes
{
    public const string Folder = "application/vnd.google-apps.folder";

    private const string WorkspacePrefix = "application/vnd.google-apps.";

    // Google Workspace files have no binary content: files.get?alt=media fails for them,
    // so they must be exported to a regular format.
    private static readonly Dictionary<string, string> ExportFormats = new(StringComparer.Ordinal)
    {
        ["application/vnd.google-apps.document"] =
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ["application/vnd.google-apps.spreadsheet"] =
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ["application/vnd.google-apps.presentation"] =
            "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ["application/vnd.google-apps.drawing"] = "application/pdf",
        ["application/vnd.google-apps.jam"] = "application/pdf",
        ["application/vnd.google-apps.script"] = "application/vnd.google-apps.script+json",
    };

    public static bool IsWorkspaceType(string? mimeType)
        => mimeType is not null && mimeType.StartsWith(WorkspacePrefix, StringComparison.Ordinal);

    public static string? GetExportMimeType(string mimeType)
        => ExportFormats.GetValueOrDefault(mimeType);
}
