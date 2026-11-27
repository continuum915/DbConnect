using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Data;

namespace DbConnect.Views;

/// <summary>
/// A collapsible sidebar section: a section-header title with a collapse chevron and optional
/// <see cref="HeaderActions"/>, over content that stretches to the full width of the column.
/// Its look is in <c>DbConnect/Styles/Controls.axaml</c>.
/// </summary>
public class SidebarSection : HeaderedContentControl
{
    public static readonly StyledProperty<object?> HeaderActionsProperty =
        AvaloniaProperty.Register<SidebarSection, object?>(nameof(HeaderActions));

    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<SidebarSection, bool>(nameof(IsExpanded), true, defaultBindingMode: BindingMode.TwoWay);

    public object? HeaderActions { get => GetValue(HeaderActionsProperty); set => SetValue(HeaderActionsProperty, value); }
    public bool IsExpanded { get => GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }
}
