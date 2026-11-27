using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services.Builders;

namespace DbConnect.Services;

public static class CsvImportExecution
{
    public static async Task<CsvImportAssessment> AssessAsync(DatabaseSession session, CsvImportOptions options)
    {
        CsvImportRisk risk;
        try
        {
            risk = session.Capabilities.CsvImport is { } dialect
                ? await dialect.AssessAsync(session, options)
                : CsvImportRisk.Unverified(options.TruncateBeforeImport);
        }
        catch (Exception)
        {
            // A failed metadata read cannot establish a rollback guarantee.
            risk = CsvImportRisk.Unverified(options.TruncateBeforeImport);
        }
        var identity = System.Text.Json.JsonSerializer.Serialize(new
        {
            session.Connection.Id,
            session.ConnectionString,
            session.ProviderName,
            options.TableName,
            options.Mode,
            options.TruncateBeforeImport,
            risk
        });
        return new(risk, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
    }

    public static string CreateTableSql(DatabaseSession session, string table, IReadOnlyList<CsvColumnSpec> specs)
    {
        var dialect = session.Capabilities.CsvImport ?? throw new NotSupportedException("Import types are not available for this provider.");
        var sql = DdlBuilderFactory.For(session.ProviderName).GenerateCreateTable(table,
            specs.Where(s => s.Include).Select(s => new ColumnDefinition
            {
                Name = s.TargetColumnName,
                DataType = dialect.TypeName(s.EffectiveType)
            }).ToList());
        return sql.TrimEnd().TrimEnd(';') + dialect.CreateTableSuffix + ";";
    }

    public static async Task<CsvImportResult> ExecuteAsync(DatabaseSession session, CsvImportOptions options,
        List<string> names, List<int> ordinals, List<string[]> rows)
    {
        var caps = session.Capabilities;
        var specs = options.ColumnSpecs.Where(s => s.Include).ToList();
        if (specs.Count == 0)
            specs = names.Select(n => new CsvColumnSpec { TargetColumnName = n }).ToList();
        var quotedTable = caps.QuoteIdentifier(options.TableName);
        var columns = string.Join(", ", names.Select(caps.QuoteIdentifier));
        var placeholders = string.Join(", ", names.Select((_, i) => caps.ParameterPlaceholder(i)));
        var inserts = new List<SqlCommandSpec>();
        // Conversion, parsing and mapping all finish before target changes.
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var parameters = new Dictionary<string, object?>();
            for (var i = 0; i < ordinals.Count; i++)
            {
                try { parameters.Add(caps.CreateParameterName(i), ConvertValue(rows[rowIndex][ordinals[i]], specs[i].EffectiveType, caps)); }
                catch (Exception ex) when (ex is FormatException or OverflowException)
                {
                    return new CsvImportResult
                    {
                        FailedDataRecord = rowIndex + 1,
                        ErrorMessage = $"Invalid {specs[i].EffectiveType} value at data record {rowIndex + 1}, source column {ordinals[i] + 1}."
                    };
                }
            }
            // Record comments distinguish identical INSERTs without embedding imported values.
            inserts.Add(new($"INSERT INTO {quotedTable} ({columns}) VALUES ({placeholders}); -- CSV data record {rowIndex + 1}", parameters));
        }
        var assessment = await AssessAsync(session, options);
        if (!assessment.Risk.FullRollback && options.RiskAcknowledgement != assessment.Token)
            return new CsvImportResult { RequiredAcknowledgement = assessment, ErrorMessage = assessment.Risk.Message };

        var commands = new List<SqlCommandSpec>();
        var retainedTable = false;
        var dataRisk = assessment.Risk;
        if (options.Mode == ImportMode.NewTable)
        {
            var ddl = CreateTableSql(session, options.TableName, specs);
            if (caps.CsvImport?.TransactionalDdl == true)
                commands.Add(new(ddl, new Dictionary<string, object?>()));
            else
            {
                // Never drop on collision or attempt automatic cleanup of the requested destination.
                try { await session.Provider.ExecuteNonQueryAsync(session.ConnectionString, ddl); retainedTable = true; }
                catch (Exception ex)
                {
                    await NotifySchemaChangeAsync(session);
                    return new CsvImportResult
                    {
                        Outcome = TransactionOutcome.Unknown,
                        ErrorMessage = $"Destination creation failed; verify its state before retrying. {ex.Message}"
                    };
                }
                // Assess the actual newly created engine before inserts; creation is already acknowledged.
                var existing = new CsvImportOptions { TableName = options.TableName, Mode = ImportMode.ExistingTable };
                var actual = await AssessAsync(session, existing);
                // The pre-write choice already disclosed unverifiable insert rollback and possible partial inserts.
                dataRisk = actual.Risk;
            }
        }
        else if (options.TruncateBeforeImport)
            commands.Add(new($"DELETE FROM {quotedTable};", new Dictionary<string, object?>()));
        commands.AddRange(inserts);
        TransactionResult result;
        try { result = await session.ExecuteTransactionAsync(commands); }
        catch (Exception ex)
        {
            if (options.Mode == ImportMode.NewTable) await NotifySchemaChangeAsync(session);
            return new CsvImportResult
            {
                Outcome = TransactionOutcome.Unknown,
                RowsImportedIsExact = false,
                ErrorMessage = $"Import outcome is uncertain. Inspect the destination before retrying. {ex.Message}" + (retainedTable ? " The requested destination table remains." : "")
            };
        }
        var failedIndex = inserts.FindIndex(c => ReferenceEquals(c, result.FailedCommand) || c.CommandText == result.FailedStatement);
        var outcome = result.Outcome;
        if (!dataRisk.FullRollback && outcome == TransactionOutcome.RolledBack)
            outcome = dataRisk.KnownNonTransactional ? TransactionOutcome.Partial : TransactionOutcome.Unknown;
        var succeeded = result.Succeeded && outcome is TransactionOutcome.Committed or TransactionOutcome.NoChanges;
        var count = CountImportedRows(result, inserts, caps.CsvImport?.DirectInsertCounts == true || dataRisk.FullRollback);
        var committedCount = succeeded || outcome == TransactionOutcome.Partial ? count.Rows : 0;
        var explanation = outcome switch
        {
            TransactionOutcome.RolledBack => "All transactional import writes were rolled back; zero rows imported.",
            TransactionOutcome.NoChanges => "No import writes were submitted.",
            TransactionOutcome.Committed => "Import committed.",
            TransactionOutcome.Partial => $"At least {committedCount} data record(s) were committed before failure; original contents may have been deleted. Inspect the destination before retrying.",
            _ => "Import outcome is uncertain. Inspect the destination before retrying; changes may remain."
        };
        if (retainedTable) explanation += " The requested destination table remains.";
        if (!assessment.Risk.FullRollback) explanation += " " + assessment.Risk.Message;
        if (options.Mode == ImportMode.NewTable && (succeeded || outcome != TransactionOutcome.NoChanges))
            await NotifySchemaChangeAsync(session);
        return new CsvImportResult
        {
            Succeeded = succeeded,
            RowsImported = committedCount,
            RowsImportedIsExact = succeeded ? count.Exact : outcome is TransactionOutcome.NoChanges or TransactionOutcome.RolledBack,
            Outcome = outcome,
            FailedDataRecord = failedIndex < 0 ? null : failedIndex + 1,
            ErrorMessage = succeeded ? null : $"{(failedIndex < 0 ? "" : $"Data record {failedIndex + 1}: ")}{result.DiagnosticMessage} {explanation}"
        };
    }

    private static (int Rows, bool Exact) CountImportedRows(TransactionResult result, List<SqlCommandSpec> inserts, bool directCounts)
    {
        if (!directCounts) return (0, inserts.Count == 0);
        var rows = 0;
        var exact = true;
        foreach (var command in inserts)
        {
            if (result.CommandRecordsAffected.TryGetValue(command, out var count) && count is 0 or 1) rows += count;
            else exact = false;
        }
        return (rows, exact);
    }

    private static async Task NotifySchemaChangeAsync(DatabaseSession session)
    {
        try { await session.SchemaChanges.InvalidateAsync(MetadataScope.Root); }
        catch (Exception ex)
        {
            // Metadata/UI refresh must never replace an established commit/rollback outcome.
            var diagnostics = DiagnosticsService.Default;
            diagnostics.RecordFailure(diagnostics.BeginOperation("csv", "refresh", session.ProviderName), ex,
                "CSV metadata refresh failed; explicit refresh is available.");
        }
    }

    private static object? ConvertValue(string value, DetectedColumnType type, DatabaseProviderCapabilities caps)
    {
        if (value.Length == 0) return null;
        return type switch
        {
            DetectedColumnType.Integer => long.Parse(value, CultureInfo.InvariantCulture),
            DetectedColumnType.Real => double.Parse(value, CultureInfo.InvariantCulture),
            DetectedColumnType.Boolean => caps.UsesBooleanKeywords
                ? value is "true" or "TRUE" or "1" ? true : value is "false" or "FALSE" or "0" ? false : throw new FormatException()
                : value is "true" or "TRUE" or "1" ? 1L : value is "false" or "FALSE" or "0" ? 0L : throw new FormatException(),
            // Drivers infer dates from ISO text in the destination context; SQLite retains its TEXT contract.
            DetectedColumnType.Date => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                ? value : throw new FormatException(),
            _ => value
        };
    }
}
