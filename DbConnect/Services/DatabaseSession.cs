using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

public sealed class DatabaseSession
{
    private readonly DiagnosticsService? _diagnostics;

    public DatabaseSession(ConnectionInfo connection, IDatabaseProvider provider, DiagnosticsService? diagnostics = null)
    {
        Connection = connection;
        Provider = provider;
        Objects = new ObjectNavigation(this);
        Catalog = new MetadataCatalog(new ProviderMetadataCatalogSource(this));
        SchemaChanges = new SchemaChangeNotifications(this);
        _diagnostics = diagnostics;
    }

    public ConnectionInfo Connection { get; }
    public IDatabaseProvider Provider { get; }
    public ObjectNavigation Objects { get; }
    public MetadataCatalog Catalog { get; }
    public SchemaChangeNotifications SchemaChanges { get; }
    public string Name => Connection.Name;
    public string ProviderName => Provider.ProviderName;
    public string ProviderIcon => Provider.ProviderIcon;
    public string DisplayName => ConnectionDisplayName.Of(this);

    /// <summary>Set for a session on a server connection that shows all databases.</summary>
    public ServerDatabaseDirectory? Server { get; set; }
    public string ConnectionString => Connection.ConnectionString;
    public DatabaseProviderCapabilities Capabilities => Provider.Capabilities;
    public string? DatabaseLabel => Provider.GetActiveDatabaseLabel(ConnectionString);
    public bool SupportsUserWrite => Capabilities.SupportsUserWrite;

    public Task<List<SchemaNode>> GetSchemaAsync() =>
        Objects.GetSchemaAsync();

    public Task<List<SchemaNode>> GetObjectsAsync(string parentName, SchemaNodeType type) =>
        Provider.GetObjectsAsync(ConnectionString, parentName, type);

    public async Task<QueryResult> ExecuteQueryAsync(string query, QueryExecutionOptions? options = null)
    {
        var context = _diagnostics?.BeginOperation("query", "execute", ProviderName, Name);
        try
        {
            var result = await Provider.ExecuteQueryAsync(ConnectionString, query, options);
            if (context != null)
            {
                if (result.Error?.IsCanceled == true)
                    _diagnostics!.Record(context, DiagnosticSeverity.Warning, "Query execution was cancelled.");
                else if (result.HasError)
                    _diagnostics!.Record(context, DiagnosticSeverity.Error, "Query execution failed.");
                else
                    _diagnostics!.Record(context, DiagnosticSeverity.Information, "Query execution completed.");
            }

            return result;
        }
        catch (Exception ex)
        {
            if (context != null)
                _diagnostics!.RecordFailure(context, ex, "Query execution raised an exception.");
            throw;
        }
    }

    public IAsyncEnumerable<QueryResult> ExecuteQueryMultipleAsync(string query, QueryExecutionOptions? options = null) =>
        Provider.ExecuteQueryMultipleAsync(ConnectionString, query, options);

    public Task<TablePageResult> LoadTablePageAsync(
        string tableName,
        IReadOnlyList<string> orderByColumns,
        int pageSize,
        int pageIndex,
        QueryExecutionOptions? options = null) =>
        Provider.LoadTablePageAsync(ConnectionString, tableName, orderByColumns, pageSize, pageIndex, options);

    public Task<int> ExecuteNonQueryAsync(string sql, MetadataScope? affectedScope = null) =>
        SchemaChanges.ExecuteAsync(sql, () => Provider.ExecuteNonQueryAsync(ConnectionString, sql), affectedScope);

    public Task<int> ExecuteNonQueryBatchAsync(string sql) =>
        ExecuteNonQueryAsync(sql);

    public Task<TransactionResult> ExecuteTransactionAsync(IReadOnlyList<string> statements) =>
        Provider.ExecuteTransactionAsync(ConnectionString, statements);

    public Task<TransactionResult> ExecuteTransactionAsync(IReadOnlyList<SqlCommandSpec> commands) =>
        Provider.ExecuteTransactionAsync(ConnectionString, commands);

    public string FormatLimitQuery(string query, int limit) =>
        Provider.FormatLimitQuery(query, limit);

    public QueryResult NormalizeExplainResult(QueryResult raw) =>
        Provider.NormalizeExplainResult(raw);

    public Task<List<IndexDefinition>> GetIndexesAsync(string tableName) =>
        Provider.GetIndexesAsync(ConnectionString, tableName);

    public Task<List<ColumnDefinition>> GetColumnDefinitionsAsync(string tableName) =>
        Provider.GetColumnDefinitionsAsync(ConnectionString, tableName);

    public Task<string?> GetViewDefinitionAsync(string viewName) =>
        Provider.GetViewDefinitionAsync(ConnectionString, viewName);

    public Task<List<string>> GetSchemasAsync() =>
        Provider.GetSchemasAsync(ConnectionString);

    public Task<string?> GetFunctionDefinitionAsync(string schema, string functionName) =>
        Provider.GetFunctionDefinitionAsync(ConnectionString, schema, functionName);

    public Task<string?> GetProcedureDefinitionAsync(string schema, string procedureName) =>
        Provider.GetProcedureDefinitionAsync(ConnectionString, schema, procedureName);

    public Task<string?> GetTriggerDefinitionAsync(string schema, string triggerName) =>
        Provider.GetTriggerDefinitionAsync(ConnectionString, schema, triggerName);

    public List<ProcedureParameter> ParseParameters(string definition) =>
        Provider.ParseParameters(definition);

    public Task SaveProcedureAsync(string definition, CancellationToken ct = default, MetadataScope? affectedScope = null) =>
        SchemaChanges.ExecuteAsync(definition, async () => { await Provider.SaveProcedureAsync(ConnectionString, definition, ct); return true; }, affectedScope);

    public string GetNewProcedureTemplate(string name) =>
        Provider.GetNewProcedureTemplate(name);

    public Task<IReadOnlyList<ProcedureParameter>> GetProcedureParametersAsync(string schema, string routineName, CancellationToken ct = default) =>
        Provider.GetProcedureParametersAsync(ConnectionString, schema, routineName, ct);

    public Task<RoutineExecutionResult> ExecuteRoutineAsync(
        string schema,
        string routineName,
        string qualifiedRoutineName,
        SchemaNodeType routineType,
        IReadOnlyList<DbParameterValue> parameters,
        CancellationToken ct = default) =>
        Provider.ExecuteRoutineAsync(ConnectionString, schema, routineName, qualifiedRoutineName, routineType, parameters, ct);

    public List<string> GetSupportedDataTypes() =>
        Provider.GetSupportedDataTypes();

    public Task<string?> GetTableCommentAsync(string tableName, CancellationToken ct = default) =>
        Provider.GetTableCommentAsync(ConnectionString, tableName, ct);

    public Task<Dictionary<string, string>> GetColumnCommentsAsync(string tableName, CancellationToken ct = default) =>
        Provider.GetColumnCommentsAsync(ConnectionString, tableName, ct);

    public Task SetTableCommentAsync(string tableName, string? comment, CancellationToken ct = default) =>
        Provider.SetTableCommentAsync(ConnectionString, tableName, comment, ct);

    public Task SetColumnCommentAsync(string tableName, string columnName, string? comment, CancellationToken ct = default) =>
        Provider.SetColumnCommentAsync(ConnectionString, tableName, columnName, comment, ct);

    public Task CreateUserAsync(string username, string password, CancellationToken ct = default) =>
        Provider.CreateUserAsync(ConnectionString, username, password, ct);

    public Task DropUserAsync(string username, CancellationToken ct = default) =>
        Provider.DropUserAsync(ConnectionString, username, ct);

    public Task ChangePasswordAsync(string username, string password, CancellationToken ct = default) =>
        Provider.ChangePasswordAsync(ConnectionString, username, password, ct);

    public Task GrantPrivilegeAsync(string username, string privilege, string? objectName, CancellationToken ct = default) =>
        Provider.GrantPrivilegeAsync(ConnectionString, username, privilege, objectName, ct);

    public Task RevokePrivilegeAsync(string username, string privilege, string? objectName, CancellationToken ct = default) =>
        Provider.RevokePrivilegeAsync(ConnectionString, username, privilege, objectName, ct);

    public string GenerateCreateUserSql(string username, string password) =>
        Provider.GenerateCreateUserSql(username, password);

    public string GenerateDropUserSql(string username) =>
        Provider.GenerateDropUserSql(username);

    public string GenerateChangePasswordSql(string username, string password) =>
        Provider.GenerateChangePasswordSql(username, password);

    public string GenerateGrantSql(string username, string privilege, string? objectName) =>
        Provider.GenerateGrantSql(username, privilege, objectName);

    public string GenerateRevokeSql(string username, string privilege, string? objectName) =>
        Provider.GenerateRevokeSql(username, privilege, objectName);
}
