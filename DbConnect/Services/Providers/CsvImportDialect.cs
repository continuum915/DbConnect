using System;
using System.Linq;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Provider-owned import types and conservative target rollback assessment.</summary>
public sealed record CsvImportDialect(string TextType, string IntegerType, string RealType,
    string DateType, string BooleanType, bool TransactionalDdl,
    Func<string, string> TargetAssessmentSql)
{
    public Func<string, string> NameLiteral { get; init; } = name => "'" + name.Replace("'", "''") + "'";
    public string CreateTableSuffix { get; init; } = "";
    public bool DirectInsertCounts { get; init; } = true;
    public string TypeName(DetectedColumnType type) => type switch
    {
        DetectedColumnType.Integer => IntegerType,
        DetectedColumnType.Real => RealType,
        DetectedColumnType.Date => DateType,
        DetectedColumnType.Boolean => BooleanType,
        _ => TextType
    };

    public async Task<CsvImportRisk> AssessAsync(DatabaseSession session, CsvImportOptions options)
    {
        if (options.Mode == ImportMode.NewTable)
            return new(TransactionalDdl, TransactionalDdl, TransactionalDdl ? "" :
                "Table creation cannot be rolled back. The requested destination may remain after failure. Insert rollback may be unverifiable; failure may leave partial inserts. All available transaction protection remains enabled.");

        // The name is a single raw identifier, including literal dots; never parse display labels.
        var literal = NameLiteral(options.TableName);
        var result = await session.ExecuteQueryAsync(TargetAssessmentSql(literal));
        if (result.HasError || result.ObjectRows.Count != 1)
            return CsvImportRisk.Unverified(options.TruncateBeforeImport);
        var row = result.ObjectRows.Single();
        var support = Convert.ToInt32(row[0]);
        var transactional = support == 1;
        var verified = transactional && Convert.ToInt32(row[1]) == 0;
        return new(verified, transactional, verified ? "" :
            (transactional ? "Trigger effects cannot be fully verified. " : "The destination does not provide verified transactional data rollback. ") +
            (options.TruncateBeforeImport ? "Failure may leave partial inserts and lose original contents during replacement." : "Failure may leave partial inserts."),
            support == -1 && Convert.ToInt32(row[1]) == 0);
    }
}
