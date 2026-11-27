using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Database routine operations, independent of other provider roles.</summary>
public interface IDatabaseRoutineProvider
{
    /// <summary>
    /// Returns the complete CREATE OR REPLACE FUNCTION / CREATE FUNCTION DDL for a named
    /// function within a schema.  Returns null when the definition cannot be retrieved.
    /// </summary>
    Task<string?> GetFunctionDefinitionAsync(string connectionString, string schema, string functionName) =>
        Task.FromResult<string?>(null);

    /// <summary>
    /// Returns the complete CREATE PROCEDURE DDL for a named stored procedure within a schema.
    /// Returns null when the definition cannot be retrieved.
    /// </summary>
    Task<string?> GetProcedureDefinitionAsync(string connectionString, string schema, string procedureName) =>
        Task.FromResult<string?>(null);

    /// <summary>
    /// Parses IN/OUT parameters from a routine definition. Empty when unsupported.
    /// </summary>
    List<ProcedureParameter> ParseParameters(string routineDefinition) =>
        RoutineDefinitionParser.ParseParameters(routineDefinition);

    /// <summary>
    /// Persists a routine definition using the provider's regular non-query execution path.
    /// </summary>
    Task SaveProcedureAsync(string connectionString, string definition, CancellationToken ct = default);

    /// <summary>
    /// Returns a provider-appropriate starter template for a new routine.
    /// </summary>
    string GetNewProcedureTemplate(string name) =>
        RoutineDefinitionParser.GetDefaultTemplate(name);

    Task<IReadOnlyList<ProcedureParameter>> GetProcedureParametersAsync(
        string connectionString,
        string schema,
        string routineName,
        CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ProcedureParameter>>(Array.Empty<ProcedureParameter>());

    Task<RoutineExecutionResult> ExecuteRoutineAsync(
        string connectionString,
        string schema,
        string routineName,
        string qualifiedRoutineName,
        SchemaNodeType routineType,
        IReadOnlyList<DbParameterValue> parameters,
        CancellationToken ct = default) => throw new NotSupportedException("Routine execution is not supported by this provider.");
}
