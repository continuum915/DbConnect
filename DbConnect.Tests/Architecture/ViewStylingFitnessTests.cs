using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DbConnect.Tests.Architecture;

/// <summary>
/// UI consistency: views take sizes, colours and spacing from the shared tokens and styles (DbConnect/Styles),
/// and icons are Fluent icons rather than Unicode glyphs. Views not migrated yet are listed in
/// <see cref="ViewStylingBudgets.Unmigrated"/>; entries may only be removed, never added.
/// </summary>
public sealed partial class ViewStylingFitnessTests
{
    private static readonly HashSet<double> SpacingScale = [0, 2, 4, 8, 12, 16];
    private static readonly string[] SpacingAttributes = ["Margin", "Padding", "Spacing", "RowSpacing", "ColumnSpacing"];
    private static readonly string[] ColourAttributes = ["Foreground", "Background", "BorderBrush", "Fill", "Stroke", "Color", "SelectionBrush", "CaretBrush"];

    /// <summary>Tool tabs that put a <c>Border.toolbar</c> band at the top (ticket 06, T1).</summary>
    private static readonly string[] ToolViews =
    [
        "QueryBuilderView", "ErDiagramView", "AutomationView", "UserManagementView", "MaskingRulesView",
        "ConnectionMonitorView", "SlowQueryDashboardView", "SchemaHistoryView", "AuditLogView",
        "DependencyAnalysisView", "FindInDataView", "SchemaDiffView", "DataCompareView", "BackupRestoreView",
        "CsvImportWizardView",
    ];

    [Fact]
    public void Views_UseNoFontSizeLiterals()
    {
        var violations = MigratedViews()
            .SelectMany(view => Attributes(view, "FontSize").Where(a => !IsMarkup(a.Value)).Select(a => Describe(view, a)))
            .ToList();

        Assert.True(violations.Count == 0, "Use a text class or a DbConnect.FontSize/DbConnect.Icon token instead of:\n" + string.Join("\n", violations));
    }

    // Icons take a DbConnect.Icon size and text a DbConnect.FontSize size, so a resource can't stand in for a literal.
    [Fact]
    public void Views_SizeIconsAndTextFromTheirTokens()
    {
        var violations = MigratedViews()
            .SelectMany(view => Attributes(view, "FontSize").Where(a => IsMarkup(a.Value))
                .Where(a => !a.Value.Contains(IsIcon(a.Element) ? "DbConnect.Icon." : "DbConnect.FontSize.", StringComparison.Ordinal))
                .Select(a => Describe(view, a)))
            .ToList();

        Assert.True(violations.Count == 0, "Size icons with DbConnect.Icon.* and text with DbConnect.FontSize.* instead of:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void Views_UseNoColourLiterals()
    {
        var violations = MigratedViews()
            .SelectMany(view => ColourAttributes.SelectMany(name => Attributes(view, name))
                .Where(a => !IsMarkup(a.Value) && a.Value != "Transparent")
                .Select(a => Describe(view, a)))
            .ToList();

        Assert.True(violations.Count == 0, "Use a DbConnect*Brush token instead of:\n" + string.Join("\n", violations));
    }

    // Controls built in code-behind (diagram cards, join lines) take their look from styles too, so they follow the theme.
    [Fact]
    public void CodeBehind_UsesNoColourOrFontSizeLiterals()
    {
        var violations = CodeBehindFiles()
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (line, index))
                .Where(x => CodeStylingLiteral().IsMatch(x.line))
                .Select(x => $"{Path.GetFileName(path)}:{x.index + 1}: {x.line.Trim()}"))
            .ToList();

        Assert.True(violations.Count == 0, "Give the control a style class (or resolve a DbConnect*Brush resource) instead of:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void Views_KeepSpacingOnTheScale()
    {
        var violations = MigratedViews()
            .SelectMany(view => SpacingAttributes.SelectMany(name => Attributes(view, name))
                .Where(a => !IsMarkup(a.Value) && !IsOnScale(a.Value))
                .Select(a => Describe(view, a)))
            .ToList();

        Assert.True(violations.Count == 0, "Use 0/2/4/8/12/16 or a DbConnect.Space/DbConnect.Thickness token instead of:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void Views_UseFluentIconsRatherThanGlyphs()
    {
        var violations = AllViews().SelectMany(GlyphIcons)
            .Concat(CodeBehindFiles().SelectMany(GlyphContentInCode))
            .ToList();

        Assert.True(violations.Count == 0, "Use an fi:SymbolIcon instead of the glyph in:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void Dialogs_HaveADefaultAndACancelButton()
    {
        var missing = Windows()
            .Where(view => !ViewStylingBudgets.WindowsWithoutDialogKeys.ContainsKey(view.Name))
            .Where(view => !HasButtonWith(view, "IsDefault") || !HasButtonWith(view, "IsCancel"))
            .Select(view => view.Name)
            .ToList();

        Assert.True(missing.Count == 0, "Give these dialogs an IsDefault and an IsCancel button (Close counts as cancel):\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Dialogs_OpenCentredOnTheirOwner()
    {
        var offCentre = Windows()
            .Where(view => view.Name != "MainWindow.axaml")
            .Where(view => view.Document.Root!.Attribute("WindowStartupLocation")?.Value != "CenterOwner")
            .Select(view => view.Name)
            .ToList();

        Assert.True(offCentre.Count == 0, "Set WindowStartupLocation=\"CenterOwner\" on:\n" + string.Join("\n", offCentre));
    }

    [Fact]
    public void ToolViews_HaveAToolbarBand()
    {
        var missing = ToolViews
            .Where(name => !Load(Path.Combine(ViewsDirectory(), name + ".axaml")).Descendants()
                .Any(e => e.Name.LocalName == "Border" && HasClass(e, "toolbar")))
            .ToList();

        Assert.True(missing.Count == 0, "Add a Border.toolbar band to:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void UnmigratedAllowList_OnlyListsViewsThatStillNeedIt()
    {
        var clean = ViewStylingBudgets.Unmigrated
            .Where(name => File.Exists(Path.Combine(ViewsDirectory(), name)))
            .Where(name => !HasLiterals(LoadView(Path.Combine(ViewsDirectory(), name))))
            .ToList();

        Assert.True(clean.Count == 0, "Remove from ViewStylingBudgets.Unmigrated (they are clean now):\n" + string.Join("\n", clean));
    }

    [Fact]
    public void DialogKeysAllowList_OnlyListsWindowsThatStillNeedIt()
    {
        var windows = Windows().ToDictionary(view => view.Name, StringComparer.Ordinal);
        var stale = ViewStylingBudgets.WindowsWithoutDialogKeys.Keys
            .Where(name => !windows.TryGetValue(name, out var view) || (HasButtonWith(view, "IsDefault") && HasButtonWith(view, "IsCancel")))
            .ToList();

        Assert.True(stale.Count == 0, "Remove from ViewStylingBudgets.WindowsWithoutDialogKeys (gone, or has both buttons now):\n" + string.Join("\n", stale));
    }

    // Icon controls (fi:SymbolIcon, PathIcon) are named "...Icon"; a view-local setter counts when its style selects one.
    private static bool IsIcon(XElement element) =>
        element.Name.LocalName == "Setter"
            ? element.Parent?.Attribute("Selector")?.Value.Contains("Icon", StringComparison.Ordinal) == true
            : element.Name.LocalName.EndsWith("Icon", StringComparison.Ordinal);

    private static bool HasLiterals(View view) =>
        Attributes(view, "FontSize").Any(a => !IsMarkup(a.Value))
        || ColourAttributes.SelectMany(name => Attributes(view, name)).Any(a => !IsMarkup(a.Value) && a.Value != "Transparent")
        || SpacingAttributes.SelectMany(name => Attributes(view, name)).Any(a => !IsMarkup(a.Value) && !IsOnScale(a.Value));

    private static IEnumerable<string> GlyphIcons(View view)
    {
        foreach (var element in view.Document.Descendants())
        {
            var name = element.Name.LocalName;
            if (name.EndsWith("Button", StringComparison.Ordinal))
            {
                var texts = element.DescendantsAndSelf()
                    .SelectMany(e => e.Attributes().Where(a => a.Name.LocalName is "Content" or "Text").Select(a => a.Value))
                    .Concat(element.DescendantNodes().OfType<XText>().Select(t => t.Value));
                if (texts.Any(HasGlyph))
                    yield return $"{view.Name}: <{name}> {Line(element)}";
            }
            else if (name == "TextBlock" && element.Attribute("Text")?.Value is { } text && IsOnlyGlyphs(text))
            {
                yield return $"{view.Name}: <TextBlock Text=\"{text}\"> {Line(element)}";
            }
        }
    }

    private static IEnumerable<string> GlyphContentInCode(string path) =>
        File.ReadLines(path)
            .Select((line, index) => (line, index))
            .Where(x => ContentAssignment().Match(x.line) is { Success: true } m && HasGlyph(m.Groups[1].Value))
            .Select(x => $"{Path.GetFileName(path)}:{x.index + 1}");

    private static bool HasGlyph(string text) => EnumerateRunes(text).Any(IsGlyph);

    private static bool IsOnlyGlyphs(string text)
    {
        var runes = EnumerateRunes(text.Trim()).ToList();
        return runes.Count > 0 && runes.All(IsGlyph);
    }

    private static IEnumerable<Rune> EnumerateRunes(string text) => text.EnumerateRunes();

    // Symbols (✕ ▶ ■ ↺ ⛔ ● ＋ …) and emoji. ASCII operators ("Ctrl+F") and punctuation such as the ellipsis are fine.
    private static bool IsGlyph(Rune rune) =>
        Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.OtherSymbol => true,
            UnicodeCategory.MathSymbol => !rune.IsAscii,
            _ => rune.Value > 0xFFFF,
        };

    private static bool IsOnScale(string value) =>
        value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
            .All(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && SpacingScale.Contains(n));

    private static bool IsMarkup(string value) => value.StartsWith('{');

    private static IEnumerable<View> Windows() =>
        AllViews().Where(view => view.Document.Root!.Name.LocalName == "Window");

    private static bool HasButtonWith(View view, string flag) =>
        view.Document.Descendants().Any(e => e.Name.LocalName.EndsWith("Button", StringComparison.Ordinal)
            && e.Attribute(flag)?.Value == "True");

    private static bool HasClass(XElement element, string cls) =>
        element.Attribute("Classes")?.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(cls) == true;

    /// <summary>Every value set for <paramref name="name"/>: as an attribute, or by a view-local style setter.</summary>
    private static IEnumerable<StyledValue> Attributes(View view, string name) =>
        view.Document.Descendants().SelectMany(e => e.Name.LocalName == "Setter"
                ? e.Attribute("Property") is { } property && e.Attribute("Value") is { } value
                    ? [new StyledValue(property.Value, value.Value, e)]
                    : []
                : e.Attributes().Select(a => new StyledValue(a.Name.LocalName, a.Value, e)))
            .Where(a => a.Name == name || a.Name.EndsWith("." + name, StringComparison.Ordinal));

    private static string Describe(View view, StyledValue value) =>
        $"{view.Name}: {value.Name}=\"{value.Value}\" {Line(value.Element)}";

    private static string Line(XElement element) =>
        element is System.Xml.IXmlLineInfo info && info.HasLineInfo() ? $"(line {info.LineNumber})" : "";

    private static IEnumerable<View> MigratedViews() =>
        AllViews().Where(view => !ViewStylingBudgets.Unmigrated.Contains(view.Name));

    private static IEnumerable<View> AllViews() =>
        Directory.GetFiles(ViewsDirectory(), "*.axaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(LoadView);

    private static IEnumerable<string> CodeBehindFiles() =>
        Directory.GetFiles(ViewsDirectory(), "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal);

    // Named by path under DbConnect/Views ("MainWindow.axaml", "Sub/Pane.axaml"), so views in subfolders stay distinct.
    private static View LoadView(string path) =>
        new(Path.GetRelativePath(ViewsDirectory(), path).Replace(Path.DirectorySeparatorChar, '/'), Load(path));

    // A view composed entirely in code-behind can be a comment-only file.
    private static XDocument Load(string path) =>
        ElementStart().IsMatch(File.ReadAllText(path))
            ? XDocument.Load(path, LoadOptions.SetLineInfo)
            : new XDocument(new XElement("Empty"));

    private static string ViewsDirectory() => Path.Combine(TestRepo.Root, "DbConnect", "Views");

    [GeneratedRegex("<[A-Za-z]")]
    private static partial Regex ElementStart();

    // Colour and size literals: Color/Brush.Parse("…"), Brushes.X or Colors.X (except Transparent), Color.FromRgb(…), FontSize = 12.
    [GeneratedRegex("""(Color|Brush)\.Parse\("|(?<![A-Za-z])(Brushes|Colors)\.(?!Transparent\b)[A-Z]|Color\.FromA?Rgb\(|FontSize\s*=\s*[0-9]""")]
    private static partial Regex CodeStylingLiteral();

    [GeneratedRegex("Content\\s*=\\s*\"([^\"]*)\"")]
    private static partial Regex ContentAssignment();

    private sealed record View(string Name, XDocument Document);

    private sealed record StyledValue(string Name, string Value, XElement Element);
}
