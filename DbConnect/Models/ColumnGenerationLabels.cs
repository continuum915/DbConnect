using System;
using System.Linq;

namespace DbConnect.Models;

/// <summary>The words the designer shows for identity kinds; empty means none.</summary>
public static class ColumnGenerationLabels
{
    public static string Of(TableDesignIdentityKind? kind) => kind switch
    {
        TableDesignIdentityKind.Always => "Always",
        TableDesignIdentityKind.ByDefault => "By default",
        TableDesignIdentityKind.Identity => "Identity",
        TableDesignIdentityKind.AutoIncrement => "Auto increment",
        TableDesignIdentityKind.RowIdAlias => "Rowid alias",
        _ => ""
    };

    public static TableDesignIdentityKind? IdentityKind(string? label) =>
        Enum.GetValues<TableDesignIdentityKind>().Cast<TableDesignIdentityKind?>()
            .FirstOrDefault(k => string.Equals(Of(k), label?.Trim(), StringComparison.OrdinalIgnoreCase));
}
