using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DbConnect.ViewModels;

namespace DbConnect.Views;

/// <summary>
/// Save / Don't Save / Cancel for unsaved work. Closing the window any other way means Cancel,
/// so the work is never discarded by accident.
/// </summary>
public partial class SaveChangesDialog : Window
{
    public SaveChangesDialog()
    {
        InitializeComponent();
    }

    /// <summary>Asks about one tab, or lists several tabs when quitting.</summary>
    public SaveChangesDialog(IReadOnlyList<string> tabTitles) : this()
    {
        if (tabTitles.Count == 1)
        {
            MessageText.Text = $"Do you want to save the changes to \"{tabTitles[0]}\"?";
            return;
        }

        MessageText.Text = $"{tabTitles.Count} tabs have unsaved changes:";
        TabList.ItemsSource = tabTitles;
        TabList.IsVisible = true;
        SaveButton.Content = "Save All";
        DiscardButton.Content = "Discard All";
    }

    private void Save_Click(object? sender, RoutedEventArgs e) => Close(SaveChangesChoice.Save);

    private void Discard_Click(object? sender, RoutedEventArgs e) => Close(SaveChangesChoice.Discard);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(SaveChangesChoice.Cancel);
}
