using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using DbConnect.Models;

namespace DbConnect.Services;

internal sealed class SqlServerRoutineProvider(IDatabaseQueryProvider queries) : DatabaseRoutineProvider(queries), IDatabaseRoutineProvider
{
    public async Task<string?> GetFunctionDefinitionAsync(string connectionString, string schema, string functionName)
    {
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var cmd = new SqlCommand(
                @"SELECT m.definition
                  FROM sys.sql_modules m
                  JOIN sys.objects o ON m.object_id = o.object_id
                  WHERE o.name = @name AND o.type IN ('FN','TF','IF')
                    AND SCHEMA_NAME(o.schema_id) = @schema;", connection);
            cmd.Parameters.AddWithValue("@name", functionName);
            cmd.Parameters.AddWithValue("@schema", schema);
            return await cmd.ExecuteScalarAsync() as string;
        }
        catch
        {
            return null;
        }
    }

    public async Task<string?> GetProcedureDefinitionAsync(string connectionString, string schema, string procedureName)
    {
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var cmd = new SqlCommand(
                @"SELECT m.definition
                  FROM sys.sql_modules m
                  JOIN sys.objects o ON m.object_id = o.object_id
                  WHERE o.name = @name AND o.type = 'P'
                    AND SCHEMA_NAME(o.schema_id) = @schema;", connection);
            cmd.Parameters.AddWithValue("@name", procedureName);
            cmd.Parameters.AddWithValue("@schema", schema);
            return await cmd.ExecuteScalarAsync() as string;
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ProcedureParameter>> GetProcedureParametersAsync(
        string connectionString,
        string schema,
        string routineName,
        CancellationToken ct = default)
    {
        var parameters = new List<ProcedureParameter>();
        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(
                @"SELECT p.name, t.name AS data_type,
                         CASE p.is_output WHEN 1 THEN 'INOUT' ELSE 'IN' END AS parameter_mode,
                         p.parameter_id
                  FROM sys.parameters p
                  JOIN sys.types t ON p.user_type_id = t.user_type_id
                  WHERE object_id = OBJECT_ID(@routineName)
                  ORDER BY p.parameter_id;", conn);
            cmd.Parameters.AddWithValue("routineName", $"{schema}.{routineName}");

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var mode = reader.GetString(2);
                parameters.Add(new ProcedureParameter
                {
                    Name = reader.GetString(0),
                    DataType = reader.GetString(1),
                    Direction = mode == "INOUT" ? DbConnect.Models.ParameterDirection.InOut : DbConnect.Models.ParameterDirection.In,
                    OrdinalPosition = reader.GetInt32(3)
                });
            }
        }
        catch { }
        return parameters;
    }

    public async Task<RoutineExecutionResult> ExecuteRoutineAsync(
        string connectionString,
        string schema,
        string routineName,
        string qualifiedRoutineName,
        SchemaNodeType routineType,
        IReadOnlyList<DbParameterValue> parameters,
        CancellationToken ct = default)
    {
        var result = new RoutineExecutionResult();
        try
        {
            await using var conn = new SqlConnection(connectionString)
            {
                FireInfoMessageEventOnUserErrors = true
            };
            conn.InfoMessage += (_, e) => result.Messages.Add($"[Info] {e.Message}");
            await conn.OpenAsync(ct);

            var targetRoutineName = string.IsNullOrWhiteSpace(qualifiedRoutineName)
                ? string.IsNullOrWhiteSpace(schema)
                    ? routineName
                    : $"{schema}.{routineName}"
                : qualifiedRoutineName;
            var executableParameters = routineType == SchemaNodeType.Function
                ? parameters.Where(parameter => parameter.Direction != DbConnect.Models.ParameterDirection.Out).ToList()
                : parameters.ToList();

            await using var cmd = conn.CreateCommand();
            var arguments = new List<string>(executableParameters.Count);
            for (var i = 0; i < executableParameters.Count; i++)
            {
                var parameter = executableParameters[i];
                var placeholder = $"@p{i}";
                arguments.Add(routineType == SchemaNodeType.Function
                    ? placeholder
                    : $"{parameter.Name} = {placeholder}{(parameter.Direction == DbConnect.Models.ParameterDirection.In ? string.Empty : " OUTPUT")}");

                var sqlParameter = cmd.CreateParameter();
                sqlParameter.ParameterName = placeholder;
                sqlParameter.Value = parameter.Value ?? DBNull.Value;
                sqlParameter.Direction = parameter.Direction switch
                {
                    DbConnect.Models.ParameterDirection.In => System.Data.ParameterDirection.Input,
                    DbConnect.Models.ParameterDirection.Out => System.Data.ParameterDirection.Output,
                    DbConnect.Models.ParameterDirection.InOut => System.Data.ParameterDirection.InputOutput,
                    _ => System.Data.ParameterDirection.Input
                };
                if (sqlParameter.Direction != System.Data.ParameterDirection.Input
                    && parameter.DataType.Contains("char", StringComparison.OrdinalIgnoreCase))
                {
                    sqlParameter.Size = 4000;
                }

                cmd.Parameters.Add(sqlParameter);
            }

            var argumentList = string.Join(", ", arguments);
            cmd.CommandText = routineType == SchemaNodeType.Function
                ? $"SELECT {targetRoutineName}({argumentList}) AS [Result];"
                : string.IsNullOrWhiteSpace(argumentList)
                    ? $"EXEC {targetRoutineName};"
                    : $"EXEC {targetRoutineName} {argumentList};";

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            int resultSetIndex = 0;
            do
            {
                if (reader.FieldCount <= 0)
                    continue;

                var queryResult = new QueryResult
                {
                    TabLabel = $"Result {++resultSetIndex}",
                    ResultIndex = resultSetIndex - 1
                };

                for (int i = 0; i < reader.FieldCount; i++)
                    queryResult.ColumnInfos.Add(new ColumnInfo { Name = reader.GetName(i), Ordinal = i });

                while (await reader.ReadAsync(ct))
                {
                    var values = new object?[reader.FieldCount];
                    for (int i = 0; i < reader.FieldCount; i++)
                        values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    queryResult.ObjectRows.Add(values);
                }

                result.ResultSets.Add(queryResult);
            } while (await reader.NextResultAsync(ct));

            foreach (SqlParameter p in cmd.Parameters)
            {
                if (p.Direction is System.Data.ParameterDirection.Output or System.Data.ParameterDirection.InputOutput)
                {
                    result.OutputParameters.Add((p.ParameterName, p.Value is DBNull ? null : p.Value));
                }
            }
        }
        catch (Exception ex)
        {
            result.Messages.Add($"[Error] {ex.Message}");
            throw;
        }

        return result;
    }
}
