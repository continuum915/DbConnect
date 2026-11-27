using Avalonia.Controls;

namespace DbConnect.Views;

public partial class AddTableDialog : Window
{
    public AddTableDialog()
    {
        InitializeComponent();
        var ok = this.FindControl<Button>("OkButton");
        var cancel = this.FindControl<Button>("CancelButton");
        if (ok != null) ok.Click += (_, _) => Close(true);
        if (cancel != null) cancel.Click += (_, _) => Close(false);
    }
}
