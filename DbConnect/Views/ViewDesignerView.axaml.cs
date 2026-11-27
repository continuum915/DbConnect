using System;
using Avalonia.Controls;
using AvaloniaEdit;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class ViewDesignerView : UserControl
{
    private TextEditor _editor;

    public ViewDesignerView()
    {
        InitializeComponent();
        _editor = this.FindControl<TextEditor>("SqlEditor")!;
        EditorPreferencesBinding.Attach(_editor);

        DataContextChanged += (s, e) =>
        {
            if (DataContext is ViewDesignerViewModel vm)
            {
                _editor.Text = vm.SelectSql;
            }
        };
    }

    private void SqlEditor_TextChanged(object? sender, EventArgs e)
    {
        if (DataContext is ViewDesignerViewModel vm)
        {
            vm.SelectSql = _editor.Text;
        }
    }
}
