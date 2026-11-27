using Avalonia.Controls;
using Avalonia.Interactivity;
using DbConnect.Models;

namespace DbConnect.Views;

public partial class CommitPreviewDialog : Window
{
    public CommitPreviewDialog() { InitializeComponent(); } // designer / AVLN3001

    public CommitPreviewDialog(CommitPreviewRequest preview)
    {
        InitializeComponent();

        var summary = this.FindControl<TextBlock>("SummaryBlock")!;
        var block = this.FindControl<TextBlock>("SqlPreviewBlock")!;
        summary.Text = preview.SummaryMessage;
        block.Text = preview.SqlPreview;

        this.FindControl<Button>("CommitButton")!.Click += (_, _) => Close(true);
        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close(false);
    }
}
