using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DbConnect.ViewModels;

namespace DbConnect.Views;

/// <summary>Chooses the tables and mode of a Dump SQL File on several selected tables.</summary>
public partial class DumpTablesDialog : Window
{
    private DumpTablesViewModel? _model;

    public DumpTablesDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_model != null) _model.CloseRequested -= Close;
        _model = DataContext as DumpTablesViewModel;
        if (_model != null) _model.CloseRequested += Close;
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
