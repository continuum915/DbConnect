using System;
using Avalonia.Controls;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class AutomationView : UserControl
{
    public AutomationView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not AutomationViewModel vm)
            return;

        vm.ShowJobEditor = async editor =>
        {
            if (TopLevel.GetTopLevel(this) is not Window window)
                return false;
            var dialog = new JobEditorDialog { DataContext = editor };
            return await dialog.ShowDialog<bool>(window);
        };
    }
}
