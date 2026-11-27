using System.Reflection;

namespace Win7AeroTheme.Tests;

public sealed class ThemeContractTests
{
    [Fact]
    public void Gallery_application_declares_theme_entrypoint()
    {
        var appMarkup = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Win7AeroTheme.Gallery",
            "App.axaml"));

        Assert.Contains("<FluentTheme />", appMarkup);
        Assert.Contains(Win7AeroTheme.StyleUri, appMarkup);
    }

    [Theory]
    [InlineData("Win7.Brush.WindowBackground")]
    [InlineData("Win7.Brush.ControlFace")]
    [InlineData("Win7.Brush.ControlFaceHover")]
    [InlineData("Win7.Brush.ControlFacePressed")]
    [InlineData("Win7.Brush.ControlBorder")]
    [InlineData("Win7.Brush.FocusGlow")]
    [InlineData("Win7.Brush.Selection")]
    [InlineData("Win7.Brush.ProgressFill")]
    [InlineData("Win7.CornerRadius.Control")]
    [InlineData("Win7.Thickness.ControlPadding")]
    [InlineData("Win7.FontFamily.UI")]
    public void Required_resource_token_is_declared(string key)
    {
        var themeDirectory = Path.Combine(FindRepositoryRoot(), "Win7AeroTheme", "Themes");
        var resourceMarkup = string.Join(
            Environment.NewLine,
            Directory.GetFiles(Path.Combine(themeDirectory, "Resources"), "*.axaml")
                .Select(File.ReadAllText));

        Assert.Contains($"x:Key=\"{key}\"", resourceMarkup);
    }

    [Theory]
    [InlineData("Button.axaml", "Button")]
    [InlineData("ToggleButton.axaml", "ToggleButton")]
    [InlineData("TextBox.axaml", "TextBox")]
    [InlineData("ComboBox.axaml", "ComboBox")]
    [InlineData("CheckBox.axaml", "CheckBox")]
    [InlineData("RadioButton.axaml", "RadioButton")]
    [InlineData("ProgressBar.axaml", "ProgressBar")]
    [InlineData("Slider.axaml", "Slider")]
    [InlineData("TabControl.axaml", "TabControl")]
    [InlineData("ListBox.axaml", "ListBox")]
    [InlineData("TreeView.axaml", "TreeView")]
    [InlineData("Menu.axaml", "Menu")]
    [InlineData("ContextMenu.axaml", "ContextMenu")]
    [InlineData("ToolTip.axaml", "ToolTip")]
    [InlineData("ScrollBar.axaml", "ScrollBar")]
    [InlineData("DataGrid.axaml", "DataGrid")]
    public void Control_style_file_targets_expected_control(string fileName, string selector)
    {
        var markup = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Win7AeroTheme",
            "Themes",
            "Controls",
            fileName));

        Assert.Contains($"Selector=\"{selector}", markup);
    }

    [Fact]
    public void Theme_entrypoint_includes_all_control_files()
    {
        var themeMarkup = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Win7AeroTheme",
            "Themes",
            "Win7AeroTheme.axaml"));

        foreach (var controlFile in Directory.GetFiles(
                     Path.Combine(FindRepositoryRoot(), "Win7AeroTheme", "Themes", "Controls"),
                     "*.axaml"))
        {
            Assert.Contains(Path.GetFileName(controlFile), themeMarkup);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Win7AeroTheme.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
