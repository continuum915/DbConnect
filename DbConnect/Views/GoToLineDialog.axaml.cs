using Avalonia.Controls;

namespace DbConnect.Views;

public partial class GoToLineDialog : Window
{
    private readonly int _maxLine;

    public GoToLineDialog() : this(1)
    {
    }

    public GoToLineDialog(int maxLine)
    {
        _maxLine = maxLine;
        InitializeComponent();

        // Go is the default button (Enter) and Cancel the cancel button (Esc).
        this.FindControl<Button>("OkButton")!.Click += (_, _) => TryConfirm();
        this.FindControl<Button>("CancelButton")!.Click += (_, _) => Close(null);
        Opened += (_, _) => this.FindControl<TextBox>("LineNumberBox")!.Focus();
    }

    private void TryConfirm()
    {
        var box = this.FindControl<TextBox>("LineNumberBox")!;
        var errorText = this.FindControl<TextBlock>("ErrorText")!;

        if (!int.TryParse(box.Text, out int line) || line < 1 || line > _maxLine)
        {
            errorText.Text = $"Enter a number between 1 and {_maxLine}.";
            errorText.IsVisible = true;
            return;
        }

        Close(line);
    }
}
