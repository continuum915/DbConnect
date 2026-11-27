using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DbConnect.ViewModels;

namespace DbConnect.Views;

/// <summary>Gives a comparison's Apply panel the destructive-action confirmation and the save dialog for its log.</summary>
internal static class ApplySyncDialogs
{
    public static void Wire(Control view, ApplySyncViewModel apply)
    {
        apply.Confirm = async preview =>
        {
            if (TopLevel.GetTopLevel(view) is not Window owner) return false;
            var confirm = new ProductionConfirmViewModel(preview);
            await new ProductionConfirmDialog { DataContext = confirm }.ShowDialog(owner);
            return confirm.Confirmed;
        };
        apply.PickLogPath = async suggested =>
        {
            if (TopLevel.GetTopLevel(view) is not { } top) return null;
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save the apply log",
                SuggestedFileName = suggested,
                FileTypeChoices = new List<FilePickerFileType>
                {
                    new("SQL with results as comments") { Patterns = ["*.sql"] },
                    new("Text") { Patterns = ["*.txt"] }
                }
            });
            return file?.TryGetLocalPath();
        };
    }
}
