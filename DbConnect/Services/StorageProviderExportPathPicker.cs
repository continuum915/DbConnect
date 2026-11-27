using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace DbConnect.Services;

/// <summary>Picks an export's save path with the window's native save dialog.</summary>
public sealed class StorageProviderExportPathPicker(TopLevel? owner, string title = "Export Data") : IExportPathPicker
{
    public async Task<string?> PickSavePathAsync(string suggestedFileName, ExportFileType fileType)
    {
        if (owner == null) return null;

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = fileType.Extension,
            FileTypeChoices =
            [
                new FilePickerFileType(fileType.Description)
                {
                    Patterns = [$"*.{fileType.Extension}"],
                    MimeTypes = [fileType.MimeType]
                }
            ]
        });

        return file?.TryGetLocalPath();
    }
}
