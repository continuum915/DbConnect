using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Compatibility facade for composed provider roles. SQL and driver behavior belong
/// to the role implementations; forwarding is shared by all registered providers.
/// </summary>
public abstract class DatabaseProviderRoles : IDatabaseSchemaProvider, IDatabaseRoutineProvider, IDatabaseAdministrationProvider, IStructuredSchemaProvider, ITableScriptSource, ITableDuplicateSource, ITableDesignSource
{
    protected abstract IDatabaseSchemaProvider Schema { get; }
    protected abstract IDatabaseRoutineProvider Routine { get; }
    protected virtual IDatabaseAdministrationProvider Administration => UnsupportedDatabaseAdministration.Instance;

    public Task<List<ColumnDefinition>> GetColumnsAsync(string connectionString, DatabaseObjectIdentity relation) =>
        ((IStructuredSchemaProvider)Schema).GetColumnsAsync(connectionString, relation);

    public Task<List<SchemaNode>> GetChildrenAsync(string connectionString, DatabaseObjectIdentity parent, SchemaNodeType kind) =>
        ((IStructuredSchemaProvider)Schema).GetChildrenAsync(connectionString, parent, kind);

    public Task<Dictionary<string, EnumColumnInfo>> GetEnumColumnsAsync(string connectionString, DatabaseObjectIdentity relation) =>
        ((IStructuredSchemaProvider)Schema).GetEnumColumnsAsync(connectionString, relation);

    public Task<TableDefinitionSnapshot> GetTableSnapshotAsync(string connectionString, DatabaseObjectIdentity table,
        CancellationToken cancellationToken = default) =>
        ((ITableScriptSource)Schema).GetTableSnapshotAsync(connectionString, table, cancellationToken);

    public Task<TableDesign> GetTableDesignAsync(string connectionString, DatabaseObjectIdentity table,
        CancellationToken cancellationToken = default) =>
        Schema is ITableDesignSource source
            ? source.GetTableDesignAsync(connectionString, table, cancellationToken)
            : throw new NotSupportedException("This provider cannot read table designs.");

    public Task<TableDuplicatePlan> PlanDuplicateAsync(string connectionString, DatabaseObjectIdentity table, string newName,
        bool includeData, CancellationToken cancellationToken = default) =>
        ((ITableDuplicateSource)Schema).PlanDuplicateAsync(connectionString, table, newName, includeData, cancellationToken);

    public Task<List<SchemaNode>> GetSchemaAsync(string connectionString) =>
        Schema.GetSchemaAsync(connectionString);

    public Task<List<SchemaNode>> GetObjectsAsync(string connectionString, string parentName, SchemaNodeType type) =>
        Schema.GetObjectsAsync(connectionString, parentName, type);

    public Task<List<IndexDefinition>> GetIndexesAsync(string connectionString, string tableName) =>
        Schema.GetIndexesAsync(connectionString, tableName);

    public Task<List<ColumnDefinition>> GetColumnDefinitionsAsync(string connectionString, string tableName) =>
        Schema.GetColumnDefinitionsAsync(connectionString, tableName);

    public Task<string?> GetViewDefinitionAsync(string connectionString, string viewName) =>
        Schema.GetViewDefinitionAsync(connectionString, viewName);

    public List<string> GetSupportedDataTypes() =>
        Schema.GetSupportedDataTypes();

    public Task<List<string>> GetSchemasAsync(string connectionString) =>
        Schema.GetSchemasAsync(connectionString);

    public Task<string?> GetTriggerDefinitionAsync(string connectionString, string schema, string triggerName) =>
        Schema.GetTriggerDefinitionAsync(connectionString, schema, triggerName);

    public Task<string?> GetTableCommentAsync(string connectionString, string tableName, CancellationToken ct = default) =>
        Schema.GetTableCommentAsync(connectionString, tableName, ct);

    public Task<Dictionary<string, string>> GetColumnCommentsAsync(string connectionString, string tableName, CancellationToken ct = default) =>
        Schema.GetColumnCommentsAsync(connectionString, tableName, ct);

    public Task<Dictionary<string, EnumColumnInfo>> GetEnumColumnsAsync(string connectionString, string tableName, CancellationToken ct = default) =>
        Schema.GetEnumColumnsAsync(connectionString, tableName, ct);

    public Task SetTableCommentAsync(string connectionString, string tableName, string? comment, CancellationToken ct = default) =>
        Schema.SetTableCommentAsync(connectionString, tableName, comment, ct);

    public Task SetColumnCommentAsync(string connectionString, string tableName, string columnName, string? comment, CancellationToken ct = default) =>
        Schema.SetColumnCommentAsync(connectionString, tableName, columnName, comment, ct);

    public Task<string?> GetFunctionDefinitionAsync(string connectionString, string schema, string functionName) =>
        Routine.GetFunctionDefinitionAsync(connectionString, schema, functionName);

    public Task<string?> GetProcedureDefinitionAsync(string connectionString, string schema, string procedureName) =>
        Routine.GetProcedureDefinitionAsync(connectionString, schema, procedureName);

    public List<ProcedureParameter> ParseParameters(string routineDefinition) =>
        Routine.ParseParameters(routineDefinition);

    public Task SaveProcedureAsync(string connectionString, string definition, CancellationToken ct = default) =>
        Routine.SaveProcedureAsync(connectionString, definition, ct);

    public string GetNewProcedureTemplate(string name) =>
        Routine.GetNewProcedureTemplate(name);

    public Task<IReadOnlyList<ProcedureParameter>> GetProcedureParametersAsync(string connectionString, string schema, string routineName, CancellationToken ct = default) =>
        Routine.GetProcedureParametersAsync(connectionString, schema, routineName, ct);

    public Task<RoutineExecutionResult> ExecuteRoutineAsync(string connectionString, string schema, string routineName, string qualifiedRoutineName, SchemaNodeType routineType, IReadOnlyList<DbParameterValue> parameters, CancellationToken ct = default) =>
        Routine.ExecuteRoutineAsync(connectionString, schema, routineName, qualifiedRoutineName, routineType, parameters, ct);

    public Task CreateUserAsync(string connectionString, string username, string password, CancellationToken ct = default) =>
        Administration.CreateUserAsync(connectionString, username, password, ct);

    public Task DropUserAsync(string connectionString, string username, CancellationToken ct = default) =>
        Administration.DropUserAsync(connectionString, username, ct);

    public Task ChangePasswordAsync(string connectionString, string username, string newPassword, CancellationToken ct = default) =>
        Administration.ChangePasswordAsync(connectionString, username, newPassword, ct);

    public Task GrantPrivilegeAsync(string connectionString, string username, string privilege, string? objectName, CancellationToken ct = default) =>
        Administration.GrantPrivilegeAsync(connectionString, username, privilege, objectName, ct);

    public Task RevokePrivilegeAsync(string connectionString, string username, string privilege, string? objectName, CancellationToken ct = default) =>
        Administration.RevokePrivilegeAsync(connectionString, username, privilege, objectName, ct);

    public string GenerateCreateUserSql(string username, string password) =>
        Administration.GenerateCreateUserSql(username, password);

    public string GenerateDropUserSql(string username) =>
        Administration.GenerateDropUserSql(username);

    public string GenerateChangePasswordSql(string username, string newPassword) =>
        Administration.GenerateChangePasswordSql(username, newPassword);

    public string GenerateGrantSql(string username, string privilege, string? objectName) =>
        Administration.GenerateGrantSql(username, privilege, objectName);

    public string GenerateRevokeSql(string username, string privilege, string? objectName) =>
        Administration.GenerateRevokeSql(username, privilege, objectName);
}
