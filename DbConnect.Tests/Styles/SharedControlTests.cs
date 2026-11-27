using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using DbConnect.Views;
using SymbolIcon = FluentIcons.Avalonia.SymbolIcon;

namespace DbConnect.Tests.Styles;

/// <summary>Ticket 02: the EmptyState and SidebarSection controls, in Light and Dark.</summary>
public sealed class SharedControlTests
{
    public static TheoryData<string> Variants => ThemedHost.Variants;

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void EmptyState_ShowsIconTitleMessageAndRunsItsAction(string variant)
    {
        var ran = false;
        var state = new EmptyState
        {
            Icon = Symbol.Flash,
            Title = "No jobs yet",
            Message = "Create a job to run SQL on a schedule.",
            ActionText = "New Job",
            ActionCommand = new RelayCommand(() => ran = true),
        };
        using var host = ThemedHost.Show(state, variant);

        var icon = Descendants<SymbolIcon>(state).Single();
        Assert.Equal(Symbol.Flash, icon.Symbol);
        Assert.Equal(40, icon.FontSize);
        var texts = Descendants<TextBlock>(state).Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("No jobs yet", texts);
        Assert.Contains("Create a job to run SQL on a schedule.", texts);
        var action = Descendants<Button>(state).Single();
        Assert.True(action.IsEffectivelyVisible);
        Assert.Equal("New Job", action.Content);
        action.Command!.Execute(null);
        Assert.True(ran);
    }

    [AvaloniaFact]
    public void EmptyState_HidesTheActionWhenThereIsNoCommand()
    {
        var state = new EmptyState { Title = "No results" };
        using var host = ThemedHost.Show(state, "Light");

        Assert.False(Descendants<Button>(state).Single().IsVisible);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void SidebarSection_StretchesToTheColumnAndCollapses(string variant)
    {
        var content = new ListBox();
        var action = new Button { Content = "+" };
        var section = new SidebarSection { Header = "Snippets", HeaderActions = action, Content = content };
        var column = new Grid { Width = 240, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, Children = { section } };
        using var host = ThemedHost.Show(column, variant);

        Assert.Equal(240, section.Bounds.Width);
        Assert.Equal(240, content.Bounds.Width);
        Assert.True(action.IsEffectivelyVisible);
        Assert.Contains(Descendants<TextBlock>(section), t => t.Text == "Snippets" && t.Classes.Contains("section-header"));

        var chevron = Descendants<ToggleButton>(section).Single();
        Assert.True(chevron.IsChecked);
        chevron.IsChecked = false;
        host.Window.UpdateLayout();
        Assert.False(section.IsExpanded);
        Assert.False(content.IsEffectivelyVisible);
    }

    // Fluent fills a checked toggle with the accent colour; the section chevron must stay flat when expanded.
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void SidebarSectionChevron_StaysFlatWhenExpanded(string variant)
    {
        var section = new SidebarSection { Header = "Snippets", Content = new TextBlock(), IsExpanded = true };
        using var host = ThemedHost.Show(section, variant);

        var chevron = Descendants<ToggleButton>(section).Single();
        Assert.True(chevron.IsChecked);
        var faces = chevron.GetVisualDescendants().OfType<Border>().Select(b => b.Background)
            .Concat(chevron.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Select(p => p.Background))
            .OfType<Avalonia.Media.ISolidColorBrush>()
            .Where(brush => brush.Color.A > 0)
            .ToList();
        Assert.True(faces.Count == 0, "Chevron face is filled: " + string.Join(", ", faces.Select(f => f.Color)));
    }

    private static IEnumerable<T> Descendants<T>(Control root) => root.GetVisualDescendants().OfType<T>();
}
