using System.Windows.Input;
using Avalonia;
using Avalonia.Controls.Primitives;
using FluentIcons.Common;

namespace DbConnect.Views;

/// <summary>
/// What an empty list, grid or pane shows instead of blank space: an icon, a title, a message saying
/// what to do next and, when <see cref="ActionCommand"/> is set, a button that does it.
/// Its look is in <c>DbConnect/Styles/Controls.axaml</c>.
/// </summary>
public class EmptyState : TemplatedControl
{
    public static readonly StyledProperty<Symbol> IconProperty =
        AvaloniaProperty.Register<EmptyState, Symbol>(nameof(Icon), Symbol.Info);

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<EmptyState, string?>(nameof(Title));

    public static readonly StyledProperty<string?> MessageProperty =
        AvaloniaProperty.Register<EmptyState, string?>(nameof(Message));

    public static readonly StyledProperty<string?> ActionTextProperty =
        AvaloniaProperty.Register<EmptyState, string?>(nameof(ActionText));

    public static readonly StyledProperty<ICommand?> ActionCommandProperty =
        AvaloniaProperty.Register<EmptyState, ICommand?>(nameof(ActionCommand));

    public Symbol Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Message { get => GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public string? ActionText { get => GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }
    public ICommand? ActionCommand { get => GetValue(ActionCommandProperty); set => SetValue(ActionCommandProperty, value); }
}
