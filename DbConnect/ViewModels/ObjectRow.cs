using System;
using System.Globalization;
using FluentIcons.Common;
using DbConnect.Models;

namespace DbConnect.ViewModels;

/// <summary>One object in the Objects tab: its navigator node (or saved query) and the metadata columns, "—" when unknown.</summary>
public sealed class ObjectRow
{
    public const string Unknown = "—";

    public ObjectRow(string name, Symbol icon, string iconRole)
    {
        Name = name;
        Icon = icon;
        IconRole = iconRole;
    }

    public string Name { get; }
    public Symbol Icon { get; }
    public string IconRole { get; }

    /// <summary>The navigator node this row stands for; null for a saved query.</summary>
    public SchemaNode? Node { get; init; }

    /// <summary>The SQL of a saved query row.</summary>
    public string? Sql { get; init; }

    public TableStatistics? Statistics { get; init; }

    public string Rows => Statistics?.Rows is { } rows ? rows.ToString("N0", CultureInfo.CurrentCulture) : Unknown;
    public string DataLength => FormatBytes(Statistics?.DataLength);
    public string IndexLength => FormatBytes(Statistics?.IndexLength);
    public string Engine => Statistics?.Engine ?? Unknown;
    public string Modified => FormatTime(Statistics?.Modified);
    public string Created => FormatTime(Statistics?.Created);
    public string Collation => Statistics?.Collation ?? Unknown;
    public string AutoIncrement => Statistics?.AutoIncrement?.ToString("N0", CultureInfo.CurrentCulture) ?? Unknown;
    public string Comment => !string.IsNullOrWhiteSpace(Statistics?.Comment) ? Statistics!.Comment!
        : !string.IsNullOrWhiteSpace(Node?.Description) ? Node!.Description!
        : Sql is { } sql ? FirstLine(sql) : Unknown;

    public static string FormatBytes(long? bytes) => bytes switch
    {
        null => Unknown,
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.##} MB",
    };

    private static string FormatTime(DateTimeOffset? time) =>
        time?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? Unknown;

    private static string FirstLine(string sql)
    {
        var line = sql.Trim().Split('\n')[0].Trim();
        return line.Length > 120 ? line[..120] + "…" : line;
    }
}
