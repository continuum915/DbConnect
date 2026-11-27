using System;
using System.IO;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>Restores a query tab's text and file binding after a restart or a crash.</summary>
internal static class FileTabRestore
{
    /// <summary>
    /// Untitled: restores <paramref name="text"/>, unsaved when <paramref name="wasDirty"/>.
    /// File tab that was clean: reloads the file from disk. File tab with unsaved changes: binds the
    /// file and restores <paramref name="text"/> over it, unsaved where it differs. Missing file:
    /// keeps <paramref name="text"/>, bound to the path, and reports the file as missing (unsaved).
    /// </summary>
    public static void Apply(QueryTabViewModel tab, string text, string? filePath, bool wasDirty)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            tab.QueryText = text;
            if (!wasDirty) tab.ClearDirty();
            return;
        }

        try
        {
            if (File.Exists(filePath))
            {
                var (fileText, document) = SqlFileService.Read(filePath);
                tab.QueryText = fileText;
                tab.ClearDirty();
                Bind(tab, document, SqlFileDiskState.InSync);
                if (wasDirty) tab.QueryText = text;
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable now: keep the saved text and treat the file as missing.
        }

        tab.QueryText = text;
        tab.ClearDirty();
        Bind(tab, SqlFileService.Missing(filePath), SqlFileDiskState.Missing);
    }

    private static void Bind(QueryTabViewModel tab, SqlFileDocument document, SqlFileDiskState state)
    {
        tab.Document = document;
        tab.FileState = state;
        tab.Title = document.FileName;
    }
}
