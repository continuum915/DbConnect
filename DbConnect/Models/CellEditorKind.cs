namespace DbConnect.Models;

/// <summary>
/// RAIN-P2-08 — Which editor control a grid cell gets, chosen from the column's
/// <see cref="ColumnDataType"/>. Several <see cref="ColumnDataType"/> values collapse onto the
/// same editor (Integer/Real/Decimal all get the same culture-invariant numeric text box).
/// </summary>
public enum CellEditorKind
{
    Text,
    Numeric,
    Boolean,
    Date,
    DateTime,
    /// <summary>RAIN-P2-09 — A dropdown listing only the ENUM/native-enum values reported by the provider.</summary>
    Enum,
    /// <summary>RAIN-P2-09 — A multi-select dropdown for a MySQL SET column's members.</summary>
    Set
}
