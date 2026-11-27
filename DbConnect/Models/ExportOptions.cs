namespace DbConnect.Models;

public enum CsvDelimiter { Comma, Tab, Pipe, Semicolon }
public enum NullRepresentation { Empty, BackslashN, NullLiteral }
public enum ExportFormat { Csv, Json, Excel, JsonLines, Markdown, SqlInsert }

public static class CsvDelimiterExtensions
{
    public static char ToSeparator(this CsvDelimiter delimiter) => delimiter switch
    {
        CsvDelimiter.Tab => '\t',
        CsvDelimiter.Pipe => '|',
        CsvDelimiter.Semicolon => ';',
        _ => ','
    };
}

public class ExportOptions : System.ComponentModel.INotifyPropertyChanged
{
    public ExportOptions Copy() => new()
    {
        Format = Format,
        Delimiter = Delimiter,
        NullRepresentation = NullRepresentation,
        IncludeHeaders = IncludeHeaders,
        AllPages = AllPages,
        SqlTableName = SqlTableName,
        RowsPerStatement = RowsPerStatement,
        IncludeColumnList = IncludeColumnList,
        SqlInsertOverride = SqlInsertOverride,
        SqlDialect = SqlDialect,
        IncludeCreateTable = IncludeCreateTable,
        CreateTableScript = CreateTableScript
    };
    private ExportFormat _format;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public bool UsesTextOptions => Format is not (ExportFormat.Json or ExportFormat.JsonLines or ExportFormat.SqlInsert);
    public bool IsSqlInsert => Format == ExportFormat.SqlInsert;
    public ExportFormat Format
    {
        get => _format;
        set
        {
            _format = value;
            PropertyChanged?.Invoke(this, new(nameof(Format)));
            PropertyChanged?.Invoke(this, new(nameof(UsesTextOptions)));
            PropertyChanged?.Invoke(this, new(nameof(IsSqlInsert)));
        }
    }
    public CsvDelimiter Delimiter { get; set; } = CsvDelimiter.Comma;
    public NullRepresentation NullRepresentation { get; set; } = NullRepresentation.Empty;
    public bool IncludeHeaders { get; set; } = true;
    public bool AllPages { get; set; } = false;

    /// <summary>SQL INSERT: the target table as SQL text, written verbatim; blank means the caller's default.</summary>
    public string SqlTableName { get; set; } = string.Empty;
    /// <summary>SQL INSERT: rows per VALUES list (1 = one statement per row), capped by the dialect.</summary>
    public int RowsPerStatement { get; set; } = 1;
    /// <summary>SQL INSERT: name the columns after the table.</summary>
    public bool IncludeColumnList { get; set; } = true;
    /// <summary>SQL INSERT: a clause written before VALUES, such as PostgreSQL's OVERRIDING SYSTEM VALUE; set by dumps, not users.</summary>
    public string? SqlInsertOverride { get; set; }
    /// <summary>SQL INSERT: the source session's dialect for identifiers and literals; not a user choice.</summary>
    public DbConnect.Services.DatabaseProviderCapabilities? SqlDialect { get; set; }
    /// <summary>SQL INSERT: write the source table's CREATE script before the inserts; table sources only.</summary>
    public bool IncludeCreateTable { get; set; }
    /// <summary>
    /// SQL INSERT: scripts the source table as CREATE for <see cref="IncludeCreateTable"/>; set by table sources and
    /// null for query results, where the option does nothing.
    /// </summary>
    public System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task<DbConnect.Services.TableScriptResult>>? CreateTableScript { get; set; }
}
