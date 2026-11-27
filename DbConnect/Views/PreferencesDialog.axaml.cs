using System;
using Avalonia.Controls;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class PreferencesDialog : Window
{
    public PreferencesDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is PreferencesViewModel vm)
            vm.CloseRequested += _ => Close();
    }
}
