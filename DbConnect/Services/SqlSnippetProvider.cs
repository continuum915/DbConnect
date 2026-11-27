using System.Collections.Generic;
using DbConnect.Models;

namespace DbConnect.Services;

public static class SqlSnippetProvider
{
    public static IReadOnlyList<SqlSnippetDefinition> GetSnippets(string? providerName)
    {
        var pagedSelect = providerName == "SQL Server"
            ? "SELECT TOP 100 *\nFROM [dbo].[table_name]\nORDER BY [Id];"
            : "SELECT *\nFROM \"table_name\"\nORDER BY \"id\"\nLIMIT 100;";

        var createView = providerName == "PostgreSQL"
            ? "CREATE OR REPLACE VIEW view_name AS\nSELECT *\nFROM table_name;"
            : "CREATE VIEW view_name AS\nSELECT *\nFROM table_name;";

        return
        [
            new SqlSnippetDefinition
            {
                Name = "SELECT",
                Description = "Basic table query",
                Text = "SELECT column_name\nFROM table_name\nWHERE condition;"
            },
            new SqlSnippetDefinition
            {
                Name = "Paged SELECT",
                Description = "Table inspection query",
                Text = pagedSelect
            },
            new SqlSnippetDefinition
            {
                Name = "INSERT",
                Description = "Insert row template",
                Text = "INSERT INTO table_name (column_a, column_b)\nVALUES (value_a, value_b);"
            },
            new SqlSnippetDefinition
            {
                Name = "UPDATE",
                Description = "Update row template",
                Text = "UPDATE table_name\nSET column_a = value_a\nWHERE id = value_id;"
            },
            new SqlSnippetDefinition
            {
                Name = "DELETE",
                Description = "Delete row template",
                Text = "DELETE FROM table_name\nWHERE id = value_id;"
            },
            new SqlSnippetDefinition
            {
                Name = "CREATE VIEW",
                Description = "View definition template",
                Text = createView
            }
        ];
    }
}
