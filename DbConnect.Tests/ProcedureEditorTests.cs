using System;
using System.Collections.Generic;
using System.Reflection;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public class ProcedureEditorTests
{
    [Fact]
    public void PostgreSqlProvider_GetNewProcedureTemplate_ContainsFunctionKeyword()
    {
        var provider = new PostgreSqlProvider();
        var template = provider.GetNewProcedureTemplate("my_func");
        Assert.Contains("my_func", template);
        Assert.Contains("CREATE", template, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MySqlProvider_GetNewProcedureTemplate_ContainsProcedureKeyword()
    {
        var provider = new MySqlProvider();
        var template = provider.GetNewProcedureTemplate("my_proc");
        Assert.Contains("my_proc", template);
        Assert.Contains("BEGIN", template, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SqliteProvider_GetNewProcedureTemplate_ReturnsComment()
    {
        var provider = new SqliteProvider();
        var template = provider.GetNewProcedureTemplate("dummy");
        Assert.NotEmpty(template);
    }

    [Fact]
    public void PostgreSqlProvider_ParseParameters_EmptyDefinition_ReturnsEmpty()
    {
        var provider = new PostgreSqlProvider();
        var result = provider.ParseParameters(string.Empty);
        Assert.Empty(result);
    }

    [Fact]
    public void PostgreSqlProvider_ParseParameters_DetectsDirectionsAndDefaults()
    {
        var provider = new PostgreSqlProvider();
        var definition = """
            CREATE OR REPLACE FUNCTION public.sample(IN customer_id integer, OUT customer_name text, INOUT attempt_count integer DEFAULT 3)
            RETURNS record
            LANGUAGE plpgsql
            AS $$
            BEGIN
                RETURN;
            END;
            $$;
            """;

        var result = provider.ParseParameters(definition);

        Assert.Collection(
            result,
            parameter =>
            {
                Assert.Equal("customer_id", parameter.Name);
                Assert.Equal(ParameterDirection.In, parameter.Direction);
                Assert.Equal("integer", parameter.DataType);
            },
            parameter =>
            {
                Assert.Equal("customer_name", parameter.Name);
                Assert.Equal(ParameterDirection.Out, parameter.Direction);
                Assert.Equal("text", parameter.DataType);
            },
            parameter =>
            {
                Assert.Equal("attempt_count", parameter.Name);
                Assert.Equal(ParameterDirection.InOut, parameter.Direction);
                Assert.Equal("3", parameter.DefaultValue);
            });
    }

    [Fact]
    public void PostgreSqlRoutineProvider_PopulateCallOutputParameters_UsesSingleCallResultRow()
    {
        var result = new RoutineExecutionResult
        {
            ResultSets =
            [
                new QueryResult
                {
                    ColumnInfos =
                    [
                        new ColumnInfo { Name = "customer_name", Ordinal = 0 },
                        new ColumnInfo { Name = "attempt_count", Ordinal = 1 }
                    ],
                    ObjectRows = [["Ada", 3]]
                }
            ]
        };
        var parameters = new List<DbParameterValue>
        {
            new("customer_id", 42, "integer", ParameterDirection.In),
            new("customer_name", null, "text", ParameterDirection.Out),
            new("attempt_count", 3, "integer", ParameterDirection.InOut)
        };

        var method = typeof(PostgreSqlRoutineProvider).GetMethod("PopulateCallOutputParameters", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        method!.Invoke(null, [result, parameters]);

        Assert.Collection(
            result.OutputParameters,
            parameter =>
            {
                Assert.Equal("customer_name", parameter.Name);
                Assert.Equal("Ada", parameter.Value);
            },
            parameter =>
            {
                Assert.Equal("attempt_count", parameter.Name);
                Assert.Equal(3, parameter.Value);
            });
    }
}
