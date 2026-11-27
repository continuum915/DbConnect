using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DbConnect.Services;

/// <summary>
/// A SQL file a query tab is bound to: where it lives, how it is encoded, its dominant line ending,
/// and the file's write time and length when DbConnect last read or wrote it.
/// </summary>
public sealed record SqlFileDocument(
    string Path,
    Encoding Encoding,
    string? NewLine,
    DateTime LastWriteTimeUtc,
    long Length,
    string? Notice = null)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>How a file-bound tab relates to its file on disk.</summary>
public enum SqlFileDiskState
{
    /// <summary>The file matches what DbConnect last read or wrote.</summary>
    InSync,
    /// <summary>The file changed on disk while the tab had unsaved edits.</summary>
    ChangedOnDisk,
    /// <summary>The file was deleted or moved.</summary>
    Missing
}

/// <summary>Reads and writes SQL files, preserving encoding, BOM and line endings; writes are atomic.</summary>
public static partial class SqlFileService
{
    private static readonly UTF8Encoding StrictUtf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>Encoding for new files: UTF-8 without a BOM.</summary>
    public static Encoding DefaultEncoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static async Task<(string Text, SqlFileDocument Document)> ReadAsync(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return Decode(fullPath, await File.ReadAllBytesAsync(fullPath));
    }

    /// <summary>Synchronous read, for restoring tabs during startup.</summary>
    public static (string Text, SqlFileDocument Document) Read(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return Decode(fullPath, File.ReadAllBytes(fullPath));
    }

    /// <summary>A placeholder for a file that no longer exists, so a restored tab stays bound to its path.</summary>
    public static SqlFileDocument Missing(string path) =>
        new(Path.GetFullPath(path), DefaultEncoding, null, DateTime.MinValue, -1);

    private static (string Text, SqlFileDocument Document) Decode(string fullPath, byte[] bytes)
    {
        var (encoding, bomLength) = DetectBom(bytes);
        string text;
        string? notice = null;
        if (encoding != null)
        {
            text = encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
        }
        else
        {
            try
            {
                text = StrictUtf8NoBom.GetString(bytes);
                encoding = DefaultEncoding;
            }
            catch (DecoderFallbackException)
            {
                encoding = FallbackEncoding();
                text = encoding.GetString(bytes);
                notice = $"This file is not valid UTF-8, so it was opened as {encoding.EncodingName}. Saving keeps that encoding.";
            }
        }

        var info = new FileInfo(fullPath);
        return (text, new SqlFileDocument(fullPath, encoding, DetectNewLine(text), info.LastWriteTimeUtc, info.Length, notice));
    }

    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="path"/> through a temporary file in the same
    /// folder, keeping <paramref name="like"/>'s encoding and line ending when given.
    /// </summary>
    public static async Task<SqlFileDocument> WriteAsync(string path, string text, SqlFileDocument? like)
    {
        var fullPath = Path.GetFullPath(path);
        var encoding = like?.Encoding ?? DefaultEncoding;
        var newLine = like?.NewLine;
        if (newLine != null) text = LineBreak().Replace(text, newLine);

        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(text);
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);

        var directory = Path.GetDirectoryName(fullPath)!;
        var temp = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllBytesAsync(temp, bytes);
        try
        {
            if (File.Exists(fullPath))
            {
                try { File.Replace(temp, fullPath, destinationBackupFileName: null); }
                catch (PlatformNotSupportedException) { File.Move(temp, fullPath, overwrite: true); }
            }
            else
            {
                File.Move(temp, fullPath);
            }
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }

        var info = new FileInfo(fullPath);
        return new SqlFileDocument(fullPath, encoding, newLine ?? DetectNewLine(text), info.LastWriteTimeUtc, info.Length,
            like?.Notice);
    }

    /// <summary>True when the file was changed or deleted since DbConnect last read or wrote it.</summary>
    public static bool HasChangedOnDisk(SqlFileDocument document)
    {
        var info = new FileInfo(document.Path);
        return !info.Exists || info.LastWriteTimeUtc != document.LastWriteTimeUtc || info.Length != document.Length;
    }

    /// <summary>Compares file paths the way the file system does: case-insensitively on Windows.</summary>
    public static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static (Encoding? Encoding, int BomLength) DetectBom(byte[] bytes) => bytes switch
    {
        [0xEF, 0xBB, 0xBF, ..] => (Utf8WithBom, 3),
        [0xFF, 0xFE, 0x00, 0x00, ..] => (Encoding.UTF32, 4),
        [0xFF, 0xFE, ..] => (Encoding.Unicode, 2),
        [0xFE, 0xFF, ..] => (Encoding.BigEndianUnicode, 2),
        _ => (null, 0)
    };

    /// <summary>The system ANSI code page on Windows; Latin-1 elsewhere.</summary>
    private static Encoding FallbackEncoding()
    {
        if (!OperatingSystem.IsWindows()) return Encoding.Latin1;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
    }

    /// <summary>The dominant line ending, or null when the text has none.</summary>
    private static string? DetectNewLine(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                else cr++;
            }
            else if (text[i] == '\n') lf++;
        }
        if (crlf == 0 && lf == 0 && cr == 0) return null;
        return crlf >= lf && crlf >= cr ? "\r\n" : lf >= cr ? "\n" : "\r";
    }

    [GeneratedRegex("\r\n|\r|\n")]
    private static partial Regex LineBreak();
}
