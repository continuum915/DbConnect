using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

public static class CsvImportService
{
    // -- Preview / Detection --

    public static Task<CsvPreviewResult> ParsePreviewAsync(string filePath, char delimiter, bool hasHeader)
    {
        return Task.Run(() =>
        {
            using var input = new StreamReader(filePath, Encoding.UTF8);
            var reader = new CsvRecordReader(input, delimiter);
            var result = new CsvPreviewResult();
            var first = reader.ReadRecord();
            if (first == null) return result;
            result.Headers = hasHeader
                ? first.ToList()
                : Enumerable.Range(1, first.Length).Select(i => $"col_{i}").ToList();

            var row = hasHeader ? reader.ReadRecord() : first;
            while (row != null)
            {
                if (result.PreviewRows.Count < 20)
                    result.PreviewRows.Add(row);
                result.TotalDataRows++;
                row = reader.ReadRecord();
            }

            return result;
        });
    }

    public static List<CsvColumnSpec> DetectColumnTypes(CsvPreviewResult preview)
    {
        return preview.Headers.Select((header, i) =>
        {
            var values = preview.PreviewRows
                .Select(r => i < r.Length ? r[i] : "")
                .Where(v => !string.IsNullOrEmpty(v))
                .ToList();

            var detected = InferType(values);
            return new CsvColumnSpec
            {
                SourceOrdinal = i,
                CsvHeader = header,
                TargetColumnName = header,
                DetectedType = detected,
                OverrideType = detected,
                Include = true
            };
        }).ToList();
    }

    private static DetectedColumnType InferType(List<string> values)
    {
        if (values.Count == 0) return DetectedColumnType.Text;
        if (values.All(v => long.TryParse(v, out _))) return DetectedColumnType.Integer;
        if (values.All(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _))) return DetectedColumnType.Real;
        if (values.All(v => DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))) return DetectedColumnType.Date;
        if (values.All(v => v is "true" or "false" or "1" or "0" or "TRUE" or "FALSE")) return DetectedColumnType.Boolean;
        return DetectedColumnType.Text;
    }

    // -- DDL Generation --

    public static string BuildCreateTableSql(string tableName, List<CsvColumnSpec> specs)
    {
        var cols = specs
            .Where(s => s.Include)
            .Select(s =>
            {
                var sqlType = s.EffectiveType switch
                {
                    DetectedColumnType.Integer => "INTEGER",
                    DetectedColumnType.Real => "REAL",
                    DetectedColumnType.Date => "TEXT",
                    DetectedColumnType.Boolean => "INTEGER",
                    _ => "TEXT"
                };
                return $"    \"{EscapeIdentifier(s.TargetColumnName)}\" {sqlType}";
            });
        return $"CREATE TABLE \"{EscapeIdentifier(tableName)}\" (\n{string.Join(",\n", cols)}\n);";
    }

    // -- Full Parse --

    public static List<string[]> ParseCsv(string filePath, char delimiter, bool hasHeader, out string[] headers)
    {
        using var input = new StreamReader(filePath, Encoding.UTF8);
        var reader = new CsvRecordReader(input, delimiter);
        var rows = new List<string[]>();
        headers = hasHeader ? reader.ReadRecord() ?? Array.Empty<string>() : Array.Empty<string>();

        string[]? row;
        while ((row = reader.ReadRecord()) != null)
            rows.Add(row);

        return rows;
    }

    // -- Import Execution --

    public static async Task<CsvImportResult> ExecuteImportAsync(
        DatabaseSession session,
        CsvImportOptions options)
    {
        try
        {
            options = SnapshotOptions(options);
            var rows = ParseCsv(options.FilePath, options.Delimiter, options.HasHeaderRow, out var fileHeaders);

            List<string> columnNames;
            if (options.ColumnSpecs.Count > 0)
                columnNames = options.ColumnSpecs.Where(s => s.Include).Select(s => s.TargetColumnName).ToList();
            else if (options.ColumnNames.Count > 0)
                columnNames = options.ColumnNames;
            else if (fileHeaders.Length > 0)
                columnNames = fileHeaders.ToList();
            else
                columnNames = Enumerable.Range(0, rows.Count > 0 ? rows[0].Length : 0).Select(i => $"col{i}").ToList();

            var fieldIndexes = BuildFieldIndexMap(options, fileHeaders, rows.Count > 0 ? rows[0].Length : fileHeaders.Length);
            if (columnNames.Count == 0)
                throw new ArgumentException("Select at least one source column.");
            if (columnNames.Any(string.IsNullOrWhiteSpace) || columnNames.Any(n => n.Contains('\0')))
                throw new ArgumentException("Target column names must be nonblank and contain no NUL characters.");
            if (columnNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != columnNames.Count)
                throw new ArgumentException("Target column names must be unique.");

            if (string.IsNullOrWhiteSpace(options.TableName) || options.TableName.Contains('\0'))
                throw new ArgumentException("A nonblank destination name without NUL characters is required.");
            return await CsvImportExecution.ExecuteAsync(session, options, columnNames, fieldIndexes, rows);
        }
        catch (Exception ex)
        {
            return new CsvImportResult { Succeeded = false, ErrorMessage = ex.Message };
        }
    }

    // -- Private Helpers --

    private static CsvImportOptions SnapshotOptions(CsvImportOptions options) => new()
    {
        FilePath = options.FilePath,
        Delimiter = options.Delimiter,
        HasHeaderRow = options.HasHeaderRow,
        Mode = options.Mode,
        TableName = options.TableName,
        TruncateBeforeImport = options.TruncateBeforeImport,
        RiskAcknowledgement = options.RiskAcknowledgement,
        ColumnNames = options.ColumnNames.ToList(),
        ColumnSpecs = options.ColumnSpecs.Select(s => new CsvColumnSpec
        {
            SourceOrdinal = s.SourceOrdinal,
            CsvHeader = s.CsvHeader,
            TargetColumnName = s.TargetColumnName,
            DetectedType = s.DetectedType,
            OverrideType = s.EffectiveType,
            Include = s.Include
        }).ToList()
    };

    private static List<int> BuildFieldIndexMap(CsvImportOptions options, string[] fileHeaders, int fieldCount)
    {
        if (options.ColumnSpecs.Count == 0)
        {
            if (options.ColumnNames.Count > 0 && options.ColumnNames.Count != fieldCount)
                throw new ArgumentException("Column names must match the source field count.");
            return Enumerable.Range(0, fieldCount).ToList();
        }

        return options.ColumnSpecs
            .Select((spec, position) => (spec, position))
            .Where(item => item.spec.Include)
            .Select(item =>
            {
                var (spec, position) = item;
                int idx;
                if (spec.SourceOrdinal.HasValue)
                    idx = spec.SourceOrdinal.Value;
                else if (!options.HasHeaderRow)
                    idx = position;
                else
                {
                    var matches = Enumerable.Range(0, fileHeaders.Length)
                        .Where(i => string.Equals(fileHeaders[i], spec.CsvHeader, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (matches.Count != 1)
                        throw new ArgumentException($"Source header '{spec.CsvHeader}' must match exactly one column; specify a source ordinal.");
                    idx = matches[0];
                }
                if (idx < 0 || idx >= fieldCount)
                    throw new ArgumentException($"Source ordinal {idx} is outside the source field count {fieldCount}.");
                return idx;
            })
            .ToList();
    }

    private static string EscapeIdentifier(string name) => name.Replace("\"", "\"\"");

}
