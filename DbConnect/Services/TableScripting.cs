using System;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Result of a scripting request: either executable SQL or a reason none was produced.</summary>
public sealed record TableScriptResult(string? Sql, string? Error)
{
    public static TableScriptResult Ok(string sql) => new(sql, null);
    public static TableScriptResult Fail(string error) => new(null, error);
}

/// <summary>Script as CREATE for tables. Never executes SQL and never returns a partial script.</summary>
public static class TableScripting
{
    public static async Task<TableScriptResult> ScriptCreateAsync(DatabaseSession session, DatabaseObjectIdentity table,
        CancellationToken cancellationToken = default)
    {
        if (session.Provider is not ITableScriptSource source)
            return TableScriptResult.Fail($"{session.ProviderName} cannot script complete table definitions.");
        try
        {
            var snapshot = await source.GetTableSnapshotAsync(session.ConnectionString, table, cancellationToken);
            return TableScriptResult.Ok(snapshot.ToScript());
        }
        catch (TableDefinitionUnavailableException ex)
        {
            return TableScriptResult.Fail(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return TableScriptResult.Fail($"Could not read the definition of {table.Name}: {DiagnosticRedactor.Redact(ex.Message)}");
        }
    }
}
