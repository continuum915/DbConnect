using System.Collections.Generic;

namespace DbConnect.Models;

public enum ImportMode { NewTable, ExistingTable }
public enum DetectedColumnType { Text, Integer, Real, Date, Boolean }

public class CsvColumnSpec
{
    private DetectedColumnType? _overrideType;
    public int? SourceOrdinal { get; set; }
    public string CsvHeader { get; set; } = "";
    public string TargetColumnName { get; set; } = "";
    public DetectedColumnType DetectedType { get; set; }
    public DetectedColumnType OverrideType { get => _overrideType ?? DetectedType; set => _overrideType = value; }
    public bool Include { get; set; } = true;
    public DetectedColumnType EffectiveType => OverrideType;
}

public class CsvPreviewResult
{
    public List<string> Headers { get; set; } = new();
    public List<string[]> PreviewRows { get; set; } = new();
    public int TotalDataRows { get; set; }
}

public class CsvImportOptions
{
    public string FilePath { get; set; } = string.Empty;
    public char Delimiter { get; set; } = ',';
    public bool HasHeaderRow { get; set; } = true;
    public ImportMode Mode { get; set; } = ImportMode.NewTable;
    public string TableName { get; set; } = string.Empty;
    public bool TruncateBeforeImport { get; set; } = false;
    public List<string> ColumnNames { get; set; } = new();
    public List<CsvColumnSpec> ColumnSpecs { get; set; } = new();
    public string? RiskAcknowledgement { get; set; }
}

public class CsvImportResult
{
    public bool Succeeded { get; set; }
    public int RowsImported { get; set; }
    public bool RowsImportedIsExact { get; set; } = true;
    public string? ErrorMessage { get; set; }
    public TransactionOutcome Outcome { get; set; } = TransactionOutcome.NoChanges;
    public int? FailedDataRecord { get; set; }
    public CsvImportAssessment? RequiredAcknowledgement { get; set; }
}

public sealed record CsvImportRisk(bool FullRollback, bool DataRollback, string Message, bool KnownNonTransactional = false)
{
    public static CsvImportRisk Unverified(bool replace) => new(false, false,
        "Rollback support could not be verified. Failure may leave partial inserts." +
        (replace ? " Replacement may lose original contents." : ""));
}

public sealed record CsvImportAssessment(CsvImportRisk Risk, string Token);
