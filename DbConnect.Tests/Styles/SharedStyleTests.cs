using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace DbConnect.Tests.Styles;

/// <summary>Ticket 02: the shared style classes in DbConnect/Styles resolve and apply in Light and Dark.</summary>
public sealed class SharedStyleTests
{
    public static TheoryData<string> Variants => ThemedHost.Variants;

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void ToolbarBorder_GetsToolbarBackgroundDividerAndPadding(string variant)
    {
        var toolbar = new Border { Classes = { "toolbar" } };
        using var host = ThemedHost.Show(toolbar, variant);

        Assert.Equal(host.Brush("DbConnectToolbarBrush"), Color(toolbar.Background));
        Assert.Equal(host.Brush("DbConnectDividerBrush"), Color(toolbar.BorderBrush));
        Assert.Equal(new Thickness(0, 0, 0, 1), toolbar.BorderThickness);
        Assert.Equal(new Thickness(6, 3), toolbar.Padding);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void ToolbarBorder_SpacesItsButtonsAndDividesGroupsWithAThinSeparator(string variant)
    {
        var separator = new Separator();
        var buttons = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { new Button(), separator, new Button() } };
        var toolbar = new Border { Classes = { "toolbar" }, Child = buttons };
        using var host = ThemedHost.Show(toolbar, variant);

        Assert.Equal(2, buttons.Spacing);
        Assert.Equal(new Size(1, 16), separator.Bounds.Size);
        Assert.Equal(host.Brush("DbConnect.Brush.Separator"), Color(separator.Background));
    }

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void TextClasses_UseTheTypeRampAndMutedBrush(string variant)
    {
        var header = new TextBlock { Classes = { "section-header" }, Text = "Columns" };
        var caption = new TextBlock { Classes = { "caption" }, Text = "12 rows" };
        var muted = new TextBlock { Classes = { "muted" }, Text = "No selection" };
        using var host = ThemedHost.Show(new StackPanel { Children = { header, caption, muted } }, variant);

        Assert.Equal(13, header.FontSize);
        Assert.Equal(FontWeight.SemiBold, header.FontWeight);
        Assert.Equal(new Thickness(0, 0, 0, 4), header.Margin);
        Assert.Equal(11, caption.FontSize);
        Assert.Equal(host.Brush("DbConnectMutedBrush"), Color(muted.Foreground));
    }

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void MutedText_HasOneBrushOnTheThemeMutedColour(string variant)
    {
        using var host = ThemedHost.Show(new TextBlock(), variant);

        Assert.Equal((Color)host.Resource("DbConnect.Color.TextMuted"), host.Brush("DbConnectMutedBrush"));
        Assert.False(host.Has("DbConnectLabelBrush"));
        Assert.False(host.Has("DbConnectSubtleBrush"));
        Assert.Equal(Colors.White, host.Brush("DbConnectOnAccentBrush"));
    }

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void CodeClasses_UseTheEditorFontFromPreferences(string variant)
    {
        var text = new TextBlock { Classes = { "code" }, Text = "SELECT 1;" };
        var box = new TextBox { Classes = { "code" }, Text = "SELECT 1;" };
        using var host = ThemedHost.Show(new StackPanel { Children = { text, box } }, variant);

        var editorFont = new FontFamily(DbConnect.Services.AppPreferences.Editor.FontFamily);
        Assert.Equal(editorFont, text.FontFamily);
        Assert.Equal(editorFont, box.FontFamily);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void ButtonClasses_GiveToolbarIconAndPrimaryButtonsTheirShape(string variant)
    {
        var toolbar = new Button { Classes = { "toolbar" }, Content = "Refresh" };
        var icon = new Button { Classes = { "toolbar", "icon" }, Content = "+" };
        var primary = new Button { Classes = { "primary" }, Content = "Save" };
        using var host = ThemedHost.Show(new StackPanel { Children = { toolbar, icon, primary } }, variant);

        Assert.Equal(24, toolbar.Bounds.Height);
        Assert.Equal(new Thickness(7, 0), toolbar.Padding);
        Assert.Equal(default, icon.Padding);
        Assert.Equal(Colors.Transparent, Color(toolbar.Background));
        Assert.Equal(new Size(24, 22), icon.Bounds.Size);
        // The primary button is outlined, not filled: an ordinary face and text with a 2 px accent border.
        Assert.Equal(host.Brush("DbConnect.Brush.Button"), Color(primary.Background));
        Assert.Equal(host.Brush("DbConnect.Brush.Text"), Color(primary.Foreground));
        Assert.Equal(host.Brush("DbConnect.Brush.Accent"), Color(primary.BorderBrush));
        Assert.Equal(new Thickness(2), primary.BorderThickness);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void DialogClasses_LayOutFormsAndRightAlignedFooters(string variant)
    {
        var label = new TextBlock { Text = "Line:" };
        var form = new Grid { Classes = { "form" }, ColumnDefinitions = new("Auto,*"), Children = { label } };
        var ok = new Button { Content = "Go" };
        var buttons = new StackPanel { Children = { ok } };
        var footer = new Border { Classes = { "dialog-footer" }, Child = buttons };
        using var host = ThemedHost.Show(new StackPanel { Children = { form, footer } }, variant);

        Assert.Equal(8, form.RowSpacing);
        Assert.Equal(8, form.ColumnSpacing);
        Assert.Equal(Avalonia.Layout.VerticalAlignment.Center, label.VerticalAlignment);
        Assert.Equal(host.Brush("DbConnectDividerBrush"), Color(footer.BorderBrush));
        Assert.Equal(new Thickness(0, 1, 0, 0), footer.BorderThickness);
        Assert.Equal(Avalonia.Layout.Orientation.Horizontal, buttons.Orientation);
        Assert.Equal(Avalonia.Layout.HorizontalAlignment.Right, buttons.HorizontalAlignment);
        Assert.Equal(8, buttons.Spacing);
        Assert.Equal(84, ok.Bounds.Width);
        Assert.Equal(26, ok.Bounds.Height);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void BannerAndSummary_UseTheirBrushesAndNoInputBorder(string variant)
    {
        var info = new Border { Classes = { "banner", "info" } };
        var warning = new Border { Classes = { "banner", "warning" } };
        var error = new Border { Classes = { "banner", "error" } };
        var summary = new Border { Classes = { "summary" } };
        using var host = ThemedHost.Show(new StackPanel { Children = { info, warning, error, summary } }, variant);

        Assert.Equal(host.Brush("DbConnectInfoNoteBrush"), Color(info.Background));
        Assert.Equal(host.Brush("DbConnectWarningBgBrush"), Color(warning.Background));
        Assert.Equal(host.Brush("DbConnectWarningBorderBrush"), Color(warning.BorderBrush));
        Assert.Equal(host.Brush("DbConnectErrorBgBrush"), Color(error.Background));
        Assert.Equal(host.Brush("DbConnectDangerBrush"), Color(error.BorderBrush));
        Assert.Equal(new Thickness(8), info.Padding);
        Assert.Equal(default, summary.BorderThickness);
        Assert.Equal(new Thickness(6, 3), summary.Padding);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void CodeTextBox_DrawsItsTextInTheEditorFont(string variant)
    {
        // The theme gives every templated control (including the ScrollViewer inside a TextBox) the UI font,
        // so the presenter that draws the text must get the code font itself.
        var box = new TextBox { Classes = { "code" }, Text = "SELECT 1;" };
        using var host = ThemedHost.Show(box, variant);

        var presenter = box.GetVisualDescendants().OfType<TextPresenter>().Single();
        Assert.Equal(new FontFamily(DbConnect.Services.AppPreferences.Editor.FontFamily), presenter.FontFamily);
    }

    private static Color Color(IBrush? brush) => ThemedHost.ColorOf(brush);
}
