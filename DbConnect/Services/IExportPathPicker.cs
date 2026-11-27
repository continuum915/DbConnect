using System.Threading.Tasks;

namespace DbConnect.Services;

/// <summary>The kind of file an export writes, as the save dialog offers it.</summary>
public sealed record ExportFileType(string Extension, string Description, string MimeType);

/// <summary>Asks the user where to save an export. Returns a local path, or null when they cancel.</summary>
public interface IExportPathPicker
{
    Task<string?> PickSavePathAsync(string suggestedFileName, ExportFileType fileType);
}
