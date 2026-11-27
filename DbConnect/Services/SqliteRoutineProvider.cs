using System.Collections.Generic;
using DbConnect.Models;

namespace DbConnect.Services;

internal sealed class SqliteRoutineProvider(IDatabaseQueryProvider queries) : DatabaseRoutineProvider(queries), IDatabaseRoutineProvider
{
    public List<ProcedureParameter> ParseParameters(string routineDefinition) =>
        RoutineDefinitionParser.ParseParameters(routineDefinition);

    public string GetNewProcedureTemplate(string name) =>
        $"-- SQLite does not support stored procedures.\n-- Use this script as a placeholder for {name}.";
}
