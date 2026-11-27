using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit;
using DbConnect.Views;

namespace DbConnect.Tests.Styles;

/// <summary>
/// Ticket 08: text in every shared style class (DbConnect/Styles) stays readable on the background it actually sits on,
/// in Light and Dark. Backgrounds are the nearest opaque brush up the visual tree, so a class that leaves its
/// background transparent is measured against the band or window under it.
/// </summary>
public sealed partial class SharedClassContrastTests
{
    /// <summary>Each shared class, in the container it is used in. The sample's text is "Sample".</summary>
    private static readonly Dictionary<string, Func<Control>> Samples = new(StringComparer.Ordinal)
    {
        ["TextBlock.section-header"] = () => Text("section-header"),
        ["TextBlock.heading"] = () => Text("heading"),
        ["TextBlock.caption"] = () => Text("caption"),
        ["TextBlock.muted"] = () => Text("muted"),
        ["TextBlock.warning-text"] = () => Text("warning-text"),
        ["TextBlock.error-text"] = () => Text("error-text"),
        ["TextBlock.code"] = () => Text("code"),
        ["Grid.form > TextBlock.hint"] = () => new Grid { Classes = { "form" }, Children = { Text("hint", "muted") } },
        ["Border.toolbar TextBlock"] = () => Band("toolbar", Text()),
        ["Border.toolbar TextBlock.section-header"] = () => Band("toolbar", Text("section-header")),
        ["Border.toolbar TextBlock.muted"] = () => Band("toolbar", Text("muted")),
        ["Border.toolbar Button.toolbar"] = () => Band("toolbar", Button("toolbar")),
        ["Border.toolbar Button.toolbar.run"] = () => Band("toolbar", Button("toolbar", "run")),
        ["Border.main-toolbar Button.tile"] = () => Band("main-toolbar", Button("tile")),
        ["Border.main-toolbar Button.tile.checked"] = () => Band("main-toolbar", Button("tile", "checked")),
        ["Border.status-bar TextBlock"] = () => Band("status-bar", Text()),
        ["Border.pane-header TextBlock"] = () => Band("pane-header", new DockPanel { Children = { Text() } }),
        ["TextBlock.node-label.emphasis"] = () => Text("node-label", "emphasis"),
        ["TextBlock.node-meta"] = () => Text("node-meta"),
        ["Border.information-pane TextBlock.subject-name"] = () => Band("information-pane", Text("subject-name")),
        ["Border.footer-strip TextBlock"] = () => Band("footer-strip", Text()),
        ["Border.field-properties TextBlock"] = () => Band("field-properties", new StackPanel { Children = { Text() } }),
        ["Border.filter-bar TextBlock.sql-keyword"] = () => Band("filter-bar", Text("sql-keyword")),
        ["TextBlock.null-cell"] = () => Text("null-cell"),
        ["StackPanel.test-result TextBlock.success"] = () => new StackPanel { Classes = { "test-result" }, Children = { Text("success") } },
        ["StackPanel.test-result TextBlock.failure"] = () => new StackPanel { Classes = { "test-result" }, Children = { Text("failure") } },
        ["Border.dirty-cell TextBlock.null-cell"] = () => Band("dirty-cell", Text("null-cell")),
        ["TextBox.dirty"] = () => new TextBox { Classes = { "dirty" }, Text = "Sample" },
        ["Border.banner.notice TextBlock"] = () => Band("banner notice", new DockPanel { Children = { Text() } }),
        ["Border.result-footer TextBlock"] = () => Band("result-footer", new DockPanel { Children = { Text() } }),
        ["ListBox.result-strip ListBoxItem:selected"] = () => new ListBox { Classes = { "result-strip" }, ItemsSource = new[] { "Sample" }, SelectedIndex = 0 },
        ["Button.link.error"] = () => Button("link", "error"),
        ["DataGridCell.code"] = () => Text("code"),
        ["MenuItem.danger"] = () => new Menu { Items = { new MenuItem { Classes = { "danger" }, Header = "Sample" } } },
        ["Border.status-bar Border.badge.production"] = () => Band("status-bar", new Border { Classes = { "badge", "production" }, Child = Text() }),
        ["Border.status-bar Border.chip.dirty"] = () => Band("status-bar", new Border { Classes = { "chip", "dirty" }, Child = Text() }),
        ["Border.toolbar ToggleButton.toolbar"] = () => Band("toolbar", new ToggleButton { Classes = { "toolbar" }, Content = "Sample" }),
        ["Border.banner.info"] = () => Band("banner info", Text()),
        ["Border.banner.warning"] = () => Band("banner warning", Text()),
        ["Border.banner.error"] = () => Band("banner error", Text()),
        ["Border.banner.inline Button"] = () => Band("banner warning inline", Button()),
        ["Border.summary"] = () => Band("summary", Text()),
        ["Border.badge.danger"] = () => Band("badge danger", Text("caption")),
        ["Border.toolbar Button.toolbar.stacked"] = () => Band("toolbar", Stacked()),
        ["Border.dialog-footer"] = () => Band("dialog-footer", new DockPanel { Children = { Text() } }),
        ["Button.primary"] = () => Button("primary"),
        ["Button.danger"] = () => Button("danger"),
        ["Button.link"] = () => Button("link"),
        ["TextBox.code"] = () => new TextBox { Classes = { "code" }, Text = "Sample" },
        ["TextBox.search"] = () => new TextBox { Classes = { "search" }, Text = "Sample" },
        ["EmptyState title"] = () => new EmptyState { Title = "Sample", Message = "Other" },
        ["EmptyState message"] = () => new EmptyState { Title = "Other", Message = "Sample" },
        ["SidebarSection header"] = () => new SidebarSection { Header = "Sample", Content = new TextBlock() },
        ["ae|TextEditor.code"] = () => new TextEditor { Classes = { "code" }, Text = "Sample" },

        // Interaction states, entered by setting the pseudo-class the styles match on.
        ["Button.primary:pointerover"] = () => InState(Button("primary"), ":pointerover"),
        ["Button.primary:pressed"] = () => InState(Button("primary"), ":pressed"),
        ["Button.danger:pointerover"] = () => InState(Button("danger"), ":pointerover"),
        ["Button.link:pointerover"] = () => InState(Button("link"), ":pointerover"),
        ["Border.toolbar Button.toolbar:pointerover"] = () => Band("toolbar", InState(Button("toolbar"), ":pointerover")),
        ["Border.toolbar ToggleButton.toolbar:checked"] = () => Band("toolbar", new ToggleButton { Classes = { "toolbar" }, Content = "Sample", IsChecked = true }),
    };

    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var variant in ThemedHost.Variants)
        {
            foreach (var name in Samples.Keys)
                data.Add(variant, name);
        }

        return data;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public void SharedClassText_IsReadableOnWhatItSitsOn(string variant, string sample)
    {
        var content = Samples[sample]();
        using var host = ThemedHost.Show(content, variant);

        // A TextBox draws its text with a TextPresenter, and an editor with its TextView, in the control's Foreground.
        var (foreground, drawnBy) = content switch
        {
            TextBox box => (box.Foreground, (Visual)box.GetVisualDescendants().OfType<TextPresenter>().Single()),
            TextEditor editor => (editor.Foreground, editor.TextArea.TextView),
            _ => SampleText(content),
        };
        Contrast.AssertReadable(foreground, BackgroundUnder(drawnBy, host), $"{sample} ({variant})");
    }

    /// <summary>Classes that style no text, and so have no contrast sample: sizes, layout, dots and icon colours.</summary>
    private static readonly Dictionary<string, string> ClassesWithoutText = new(StringComparer.Ordinal)
    {
        ["icon"] = "icon-only button size",
        ["field"] = "form field width",
        ["short"] = "form field width",
        ["start"] = "footer layout",
        ["status-dot"] = "a dot, no text",
        ["connection"] = "a dot, no text",
        ["connected"] = "a dot, no text",
        ["caret"] = "icon size and colour",
        ["tile-separator"] = "a line, no text",
        ["main-toolbar"] = "toolbar band; tile text is sampled on it",
        ["node-row"] = "navigator row layout; its text is sampled as node-label and node-meta",
        ["node-icon"] = "icon size and role colour",
        ["tag"] = "a colour square, no text",
        ["filter-band"] = "padding and a divider around a filter box",
        ["navigator"] = "tree layout; row text is sampled as node-label",
        ["information-pane"] = "pane background; its text is sampled as subject-name and on Panel in PaletteContrastTests",
        ["information-subject"] = "layout of the subject icon and name",
        ["subject"] = "icon size",
        ["information-item"] = "key/value grid layout",
        ["object-name"] = "icon and name spacing",
        ["role"] = "icon colour",
        ["number"] = "cell alignment only",
        ["dialog-page"] = "dialog tab layout",
        ["provider-tile"] = "layout; the provider name is sampled as subject-name",
        ["provider-icon"] = "an icon tile, no text",
        ["swatches"] = "colour swatches, no text",
        ["swatch"] = "a colour square, no text",
        ["test-result"] = "layout; its text is sampled as success and failure",
        ["sql-filter"] = "form layout; its labels are sampled as sql-keyword",
        ["flyout-panel"] = "flyout layout",
        ["cell-box"] = "record pane layout",
        ["record-field"] = "record pane layout",
        ["null"] = "italic only; null text colour is sampled as null-cell",
        ["segmented"] = "joins toggle buttons; their text is sampled as ToggleButton.toolbar",
        ["active"] = "icon colours while a transaction is open",
        ["commit"] = "icon colour",
        ["rollback"] = "icon colour",
        ["status-lead"] = "status bar layout; status text is sampled in the status bar",
        ["status-segment"] = "a divider line; status text is sampled in the status bar",
        ["checked"] = "tile state; sampled as Button.tile.checked",
        ["transaction"] = "a dot, no text",
        ["delete"] = "icon colour",
        ["inline"] = "icon size and compact banner padding",
        ["document-tabs"] = "tab strip layout; tab text is checked by ThemeContrastTests",
    };

    [Fact]
    public void EverySharedClass_HasAContrastSample()
    {
        var covered = string.Join(" ", Samples.Keys);
        var missing = SharedStyleSelectors()
            .SelectMany(selector => ClassName().Matches(selector).Select(m => m.Groups[1].Value)
                .Concat(SharedControl().Matches(selector).Select(m => m.Groups[1].Value)))
            .Distinct(StringComparer.Ordinal)
            .Where(name => !ClassesWithoutText.ContainsKey(name))
            .Where(name => !Regex.IsMatch(covered, $@"(?<![\w-]){Regex.Escape(name)}(?![\w-])"))
            .ToList();

        Assert.True(missing.Count == 0, "Add a contrast sample (or a reason in ClassesWithoutText) for:\n" + string.Join("\n", missing));
    }

    private static IEnumerable<string> SharedStyleSelectors()
    {
        var styles = Path.Combine(TestRepo.Root, "DbConnect", "Styles");
        return Directory.GetFiles(styles, "*.axaml")
            .SelectMany(path => XDocument.Load(path).Descendants())
            .Where(e => e.Name.LocalName == "Style")
            .Select(e => e.Attribute("Selector")?.Value ?? "");
    }

    [GeneratedRegex(@"\.([a-z][a-z0-9-]*)")]
    private static partial Regex ClassName();

    [GeneratedRegex(@"views\|([A-Za-z]+)")]
    private static partial Regex SharedControl();

    private static (IBrush? Foreground, Visual DrawnBy) SampleText(Control content)
    {
        var text = content.GetSelfAndVisualDescendants().OfType<TextBlock>()
            .First(block => block.Text == "Sample" && block.IsEffectivelyVisible);
        return (text.Foreground, text);
    }

    /// <summary>
    /// What the control's text is drawn on: the nearest opaque background at or above it (ending at the window's),
    /// with any translucent washes between (Fluent's hover overlay) blended over it.
    /// </summary>
    private static IBrush BackgroundUnder(Visual visual, ThemedHost host)
    {
        var washes = new List<Color>();
        foreach (var ancestor in visual.GetSelfAndVisualAncestors())
        {
            var brush = ancestor switch
            {
                Border border => border.Background,
                Panel panel => panel.Background,
                ContentPresenter presenter => presenter.Background,
                TemplatedControl control => control.Background,
                _ => null,
            };
            if (brush is ISolidColorBrush solid && solid.Color.A * solid.Opacity < 255)
            {
                if (solid.Color.A > 0 && solid.Opacity > 0)
                    washes.Add(Color.FromArgb((byte)(solid.Color.A * solid.Opacity), solid.Color.R, solid.Color.G, solid.Color.B));
            }
            else if (brush is not null)
            {
                return Blend(washes, brush);
            }
        }

        return Blend(washes, host.Window.Background ?? (IBrush)host.Resource("DbConnect.Brush.Surface"));
    }

    /// <summary>Paints the washes (nearest first) over each colour of the opaque base.</summary>
    private static IBrush Blend(List<Color> washes, IBrush opaque)
    {
        if (washes.Count == 0)
            return opaque;

        var stops = Contrast.ColoursOf(opaque)
            .Select(colour => Enumerable.Reverse(washes).Aggregate(colour, Over))
            .Select((colour, i) => new GradientStop(colour, i));
        return new LinearGradientBrush { GradientStops = [.. stops] };
    }

    private static Color Over(Color under, Color wash)
    {
        var a = wash.A / 255.0;
        byte Mix(byte top, byte bottom) => (byte)Math.Round((top * a) + (bottom * (1 - a)));
        return Color.FromRgb(Mix(wash.R, under.R), Mix(wash.G, under.G), Mix(wash.B, under.B));
    }

    private static T InState<T>(T control, string pseudoClass)
        where T : Control
    {
        ((IPseudoClasses)control.Classes).Set(pseudoClass, true);
        return control;
    }

    private static Button Stacked() => new()
    {
        Classes = { "toolbar", "stacked" },
        Content = new StackPanel { Children = { new FluentIcons.Avalonia.SymbolIcon { Symbol = FluentIcons.Common.Symbol.Play }, Text() } },
    };

    private static TextBlock Text(params string[] classes)
    {
        var text = new TextBlock { Text = "Sample" };
        text.Classes.AddRange(classes);
        return text;
    }

    private static Button Button(params string[] classes)
    {
        var button = new Button { Content = "Sample" };
        button.Classes.AddRange(classes);
        return button;
    }

    private static Border Band(string classes, Control child)
    {
        var border = new Border { Child = child };
        border.Classes.AddRange(classes.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return border;
    }
}
