using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DbConnect.Views;

/// <summary>Runs a command each time a menu item's submenu opens (the Favorites menu reloads its list).</summary>
public static class MenuOpenedCommand
{
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<MenuItem, ICommand?>("Command", typeof(MenuOpenedCommand));

    static MenuOpenedCommand()
    {
        CommandProperty.Changed.AddClassHandler<MenuItem>((item, e) =>
        {
            item.SubmenuOpened -= OnSubmenuOpened;
            if (e.NewValue is ICommand)
                item.SubmenuOpened += OnSubmenuOpened;
        });
    }

    public static ICommand? GetCommand(MenuItem item) => item.GetValue(CommandProperty);

    public static void SetCommand(MenuItem item, ICommand? value) => item.SetValue(CommandProperty, value);

    private static void OnSubmenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && ReferenceEquals(e.Source, item) && GetCommand(item) is { } command
            && command.CanExecute(null))
            command.Execute(null);
    }
}
