namespace DbConnect.Models;

public enum SchemaContextAction
{
    CreateTable,
    DesignTable,
    DropObject,
    CopyName,
    ScriptSelect,
    ManageIndexes,
    CreateView,
    EditView,
    CreateRoutine,
    RefreshNode,
    RefreshConnection,
    ScriptCreate,
    ScriptDrop,
    ImportCsv,
    ImportCsvIntoTable,
    ScriptPragma,
    ViewDependencies,
    CompareData,
    BackupRestore
}

public sealed record SchemaActionAvailability(bool IsVisible, bool IsEnabled, string? DisabledReason = null);
