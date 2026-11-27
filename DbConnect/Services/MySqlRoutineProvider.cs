using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;
using DbConnect.Models;

namespace DbConnect.Services;

internal sealed class MySqlRoutineProvider(IDatabaseQueryProvider queries) : DatabaseRoutineProvider(queries), IDatabaseRoutineProvider
{
    public async Task<string?> GetFunctionDefinitionAsync(string connectionString, string schema, string functionName)
    {
        try
        {
            await using var connection = MySqlConnections.Create(connectionString);
            await connection.OpenAsync();
            var db = string.IsNullOrEmpty(schema) ? connection.Database : schema;
            await using var cmd = connection.CreateCommand();
            // SHOW CREATE FUNCTION returns: Function | sql_mode | Create Function | ...
            cmd.CommandText = $"SHOW CREATE FUNCTION `{db.Replace("`", "``")}`.`{functionName.Replace("`", "``")}`;";
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                return reader.IsDBNull(2) ? null : reader.GetString(2);
            return null;
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
            await using var connection = MySqlConnections.Create(connectionString);
            await connection.OpenAsync();
            var db = string.IsNullOrEmpty(schema) ? connection.Database : schema;
            await using var cmd = connection.CreateCommand();
            // SHOW CREATE PROCEDURE returns: Procedure | sql_mode | Create Procedure | ...
            cmd.CommandText = $"SHOW CREATE PROCEDURE `{db.Replace("`", "``")}`.`{procedureName.Replace("`", "``")}`;";
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
                return reader.IsDBNull(2) ? null : reader.GetString(2);
            return null;
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
            await using var conn = MySqlConnections.Create(connectionString);
            await conn.OpenAsync(ct);
            var db = string.IsNullOrEmpty(schema) ? conn.Database : schema;
            await using var cmd = new MySqlCommand(
                @"SELECT parameter_name, data_type, parameter_mode, ordinal_position
                  FROM information_schema.parameters
                  WHERE specific_schema = @db AND specific_name = @routineName
                  ORDER BY ordinal_position;", conn);
            cmd.Parameters.AddWithValue("db", db);
            cmd.Parameters.AddWithValue("routineName", routineName);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var mode = reader.IsDBNull(2) ? "IN" : reader.GetString(2).ToUpperInvariant();
                parameters.Add(new ProcedureParameter
                {
                    Name = reader.IsDBNull(0) ? $"p{reader.GetInt32(3)}" : reader.GetString(0),
                    DataType = reader.GetString(1),
                    Direction = mode switch
                    {
                        "IN" => DbConnect.Models.ParameterDirection.In,
                        "OUT" => DbConnect.Models.ParameterDirection.Out,
                        "INOUT" => DbConnect.Models.ParameterDirection.InOut,
                        _ => DbConnect.Models.ParameterDirection.In
                    },
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
            await using var conn = MySqlConnections.Create(connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = new MySqlCommand(routineName, conn);
            cmd.CommandType = CommandType.StoredProcedure;

            foreach (var p in parameters)
            {
                var param = cmd.CreateParameter();
                param.ParameterName = p.Name;
                param.Value = p.Value ?? DBNull.Value;
                param.Direction = p.Direction switch
                {
                    DbConnect.Models.ParameterDirection.In => System.Data.ParameterDirection.Input,
                    DbConnect.Models.ParameterDirection.Out => System.Data.ParameterDirection.Output,
                    DbConnect.Models.ParameterDirection.InOut => System.Data.ParameterDirection.InputOutput,
                    _ => System.Data.ParameterDirection.Input
                };
                cmd.Parameters.Add(param);
            }

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            int resultSetIndex = 0;
            do
            {
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

            foreach (MySqlParameter p in cmd.Parameters)
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

    public List<ProcedureParameter> ParseParameters(string routineDefinition) =>
        RoutineDefinitionParser.ParseParameters(routineDefinition);

    public string GetNewProcedureTemplate(string name) =>
        $"CREATE PROCEDURE `{name}`()\nBEGIN\n  -- TODO\nEND;";
}
