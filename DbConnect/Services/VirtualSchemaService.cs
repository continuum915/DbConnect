using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

public class VirtualSchemaService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<VirtualSchema?> LoadAsync(string path)
    {
        var document = await LoadModelAsync(path);
        return document?.Schema;
    }

    public async Task<DbConnectModelDocument?> LoadModelAsync(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(path);
            var document = JsonSerializer.Deserialize<DbConnectModelDocument>(json);
            if (document?.Schema != null)
                return document;

            var schema = JsonSerializer.Deserialize<VirtualSchema>(json);
            return schema == null ? null : new DbConnectModelDocument { Schema = schema };
        }
        catch
        {
            return null;
        }
    }

    public Task SaveAsync(VirtualSchema schema, string path) =>
        SaveModelAsync(schema, new DiagramWorkspace(), path);

    public async Task SaveModelAsync(VirtualSchema schema, DiagramWorkspace workspace, string path)
    {
        var json = JsonSerializer.Serialize(new DbConnectModelDocument
        {
            Schema = schema,
            Workspace = workspace
        }, JsonOptions);

        await File.WriteAllTextAsync(path, json);
    }

    public string GenerateDdl(VirtualSchema schema, string providerName)
    {
        var statements = new List<string>();

        foreach (var table in schema.Tables)
            statements.Add(DdlGenerator.GenerateCreateTable(table.Name, table.Columns, providerName).TrimEnd());

        foreach (var relationship in schema.Relationships)
            statements.Add(GenerateAddForeignKey(relationship, providerName));

        return string.Join(Environment.NewLine + Environment.NewLine, statements.Where(statement => !string.IsNullOrWhiteSpace(statement)));
    }

    public string GenerateAddForeignKey(VirtualRelationship relationship, string providerName)
    {
        var constraintName = string.IsNullOrWhiteSpace(relationship.Name)
            ? $"fk_{relationship.FromTable}_{relationship.FromColumn}"
            : relationship.Name;

        return new StringBuilder()
            .Append("ALTER TABLE ")
            .Append(DdlGenerator.Quote(relationship.FromTable, providerName))
            .Append(" ADD CONSTRAINT ")
            .Append(DdlGenerator.Quote(constraintName, providerName))
            .Append(" FOREIGN KEY (")
            .Append(DdlGenerator.Quote(relationship.FromColumn, providerName))
            .Append(") REFERENCES ")
            .Append(DdlGenerator.Quote(relationship.ToTable, providerName))
            .Append('(')
            .Append(DdlGenerator.Quote(relationship.ToColumn, providerName))
            .Append(");")
            .ToString();
    }
}
