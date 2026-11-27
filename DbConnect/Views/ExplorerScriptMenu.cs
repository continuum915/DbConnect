using System.Linq;
using Avalonia.Controls;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Views;

/// <summary>Builds the explorer's Script As submenu: one entry per script kind, each with its targets.</summary>
internal static class ExplorerScriptMenu
{
    private static readonly (ScriptTarget Target, string Header)[] Targets =
    [
        (ScriptTarget.NewTab, "New Query Tab"),
        (ScriptTarget.Clipboard, "Clipboard"),
        (ScriptTarget.File, "File…")
    ];

    public static void Populate(MenuItem menu, MainWindowViewModel vm, SchemaNode node, SchemaActionAvailability availability)
    {
        var kinds = ObjectScriptText.KindsFor((node.RealNode ?? node).Type);
        menu.IsVisible = availability.IsVisible && kinds.Count > 0;
        menu.IsEnabled = availability.IsEnabled;
        ToolTip.SetTip(menu, availability.DisabledReason);
        menu.Items.Clear();
        foreach (var kind in kinds)
        {
            var item = new MenuItem { Header = kind.ToString().ToUpperInvariant() };
            foreach (var (target, header) in Targets)
            {
                var targetItem = new MenuItem { Header = header };
                targetItem.Click += async (_, _) => await vm.Tools.ScriptObjectAsync(node, kind, target);
                item.Items.Add(targetItem);
            }
            menu.Items.Add(item);
        }
    }

    /// <summary>Supplies the clipboard, save picker and status line that scripts sent elsewhere need.</summary>
    public static void Wire(Window owner, MainWindowViewModel vm)
    {
        vm.Tools.CopyToClipboardAsync = text => TopLevel.GetTopLevel(owner)!.Clipboard!.SetTextAsync(text);
        vm.Tools.PickScriptPathAsync = suggestedName => vm.Files.PickSavePath?.Invoke(suggestedName)
                                                        ?? System.Threading.Tasks.Task.FromResult<string?>(null);
        vm.Tools.ReportStatus = message =>
        {
            vm.StatusMessage = message;
            vm.StatusHasError = false;
        };
    }

    internal static string[] Headers(MenuItem menu) =>
        menu.Items.OfType<MenuItem>().Select(i => i.Header?.ToString() ?? "").ToArray();
}
