using Avalonia.Controls;
using DbConnect.Models;

namespace DbConnect.Views;

/// <summary>
/// RAIN-P2-12 — Asks how to resolve a row that changed underneath the user: reload it (dropping the
/// edit), overwrite the other change, or keep editing. Keeping editing, the default, closes with null.
/// </summary>
public partial class ConcurrencyConflictDialog : Window
{
    public ConcurrencyConflictDialog() { InitializeComponent(); } // designer / AVLN3001

    public ConcurrencyConflictDialog(ConcurrencyConflictRequest conflict) : this()
    {
        MessageText.Text = conflict.Message;
        OverwriteButton.IsVisible = conflict.CanOverwrite;

        ReloadButton.Click += (_, _) => Close(ConcurrencyConflictChoice.ReloadRow);
        OverwriteButton.Click += (_, _) => Close(ConcurrencyConflictChoice.Overwrite);
        CancelButton.Click += (_, _) => Close(null);
    }
}
