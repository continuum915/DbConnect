using System.IO;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using TextMateSharp.Internal.Themes.Reader;
using TextMateSharp.Themes;

namespace DbConnect.Views;

/// <summary>
/// The editor's TextMate syntax theme, built from the theme's SQL palette roles (<c>DbConnect.Color.SqlKeyword</c>,
/// <c>SqlFunction</c>, <c>SqlString</c>, <c>SqlNumber</c>; comments in <c>TextNull</c> italic) for a theme variant, so
/// highlighting follows the palette instead of a bundled VS Code theme.
/// </summary>
internal static class EditorSyntaxTheme
{
    /// <summary>TextMate scopes coloured by each palette role, with an optional font style.</summary>
    private static readonly (string Role, string? FontStyle, string[] Scopes)[] Rules =
    [
        ("TextNull", "italic", ["comment", "punctuation.definition.comment"]),
        ("SqlString", null, ["string", "punctuation.definition.string"]),
        ("SqlNumber", null, ["constant.numeric"]),
        ("SqlKeyword", "bold", ["keyword", "storage", "keyword.other", "keyword.operator.logical", "keyword.operator.word",
            "constant.language", "support.type.property-name"]),
        ("SqlFunction", null, ["support.function", "entity.name.function"]),
        ("TextSecondary", null, ["keyword.operator", "punctuation"]),
    ];

    public static IRawTheme For(IResourceHost host, ThemeVariant variant)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("name", $"DbConnect {variant}");
            json.WriteStartArray("tokenColors");
            WriteRule(json, null, Hex(host, variant, "Text"), null);
            foreach (var (role, fontStyle, scopes) in Rules)
                WriteRule(json, scopes, Hex(host, variant, role), fontStyle);
            json.WriteEndArray();
            json.WriteEndObject();
        }

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, Encoding.UTF8);
        return ThemeReader.ReadThemeSync(reader);
    }

    private static void WriteRule(Utf8JsonWriter json, string[]? scopes, string foreground, string? fontStyle)
    {
        json.WriteStartObject();
        if (scopes != null)
        {
            json.WriteStartArray("scope");
            foreach (var scope in scopes)
                json.WriteStringValue(scope);
            json.WriteEndArray();
        }

        json.WriteStartObject("settings");
        json.WriteString("foreground", foreground);
        if (fontStyle != null)
            json.WriteString("fontStyle", fontStyle);
        json.WriteEndObject();
        json.WriteEndObject();
    }

    private static string Hex(IResourceHost host, ThemeVariant variant, string role)
    {
        var colour = host.TryFindResource($"DbConnect.Color.{role}", variant, out var value) && value is Color c ? c : default;
        return $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}";
    }
}
