using System;
using System.IO;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;
using Xunit;

namespace DbConnect.Tests;

public class VirtualSchemaTests
{
    [Fact]
    public async Task SaveAndLoad_PreservesTableNames()
    {
        var service = new VirtualSchemaService();
        var path = CreateArtifactPath();

        try
        {
            var schema = new VirtualSchema
            {
                Name = "MyModel",
                Tables = new() { new VirtualTable { Name = "customers" } }
            };

            await service.SaveAsync(schema, path);
            var loaded = await service.LoadAsync(path);

            Assert.NotNull(loaded);
            Assert.Single(loaded!.Tables);
            Assert.Equal("customers", loaded.Tables[0].Name);
        }
        finally
        {
            DeleteIfExists(path);
        }
    }

    [Fact]
    public async Task SaveAndLoad_PreservesRelationships()
    {
        var service = new VirtualSchemaService();
        var path = CreateArtifactPath();

        try
        {
            var schema = new VirtualSchema
            {
                Relationships = new()
                {
                    new VirtualRelationship
                    {
                        FromTable = "orders",
                        FromColumn = "customer_id",
                        ToTable = "customers",
                        ToColumn = "id"
                    }
                }
            };

            await service.SaveAsync(schema, path);
            var loaded = await service.LoadAsync(path);

            Assert.NotNull(loaded);
            Assert.Single(loaded!.Relationships);
            Assert.Equal("orders", loaded.Relationships[0].FromTable);
        }
        finally
        {
            DeleteIfExists(path);
        }
    }

    [Fact]
    public async Task SaveModelAndLoadModel_PreservesWorkspaceLayout()
    {
        var service = new VirtualSchemaService();
        var path = CreateArtifactPath();

        try
        {
            var schema = new VirtualSchema
            {
                Name = "WorkspaceModel",
                Tables = new() { new VirtualTable { Name = "products", X = 120, Y = 240 } }
            };
            var workspace = new DiagramWorkspace
            {
                HiddenTables = new() { "audit_log" },
                Notes = new() { new DiagramNote { Text = "Review indexes", X = 10, Y = 20 } }
            };

            await service.SaveModelAsync(schema, workspace, path);
            var loaded = await service.LoadModelAsync(path);

            Assert.NotNull(loaded);
            Assert.Equal("WorkspaceModel", loaded!.Schema.Name);
            Assert.Single(loaded.Workspace.HiddenTables);
            Assert.Single(loaded.Workspace.Notes);
            Assert.Equal("Review indexes", loaded.Workspace.Notes[0].Text);
        }
        finally
        {
            DeleteIfExists(path);
        }
    }

    [Fact]
    public void GenerateDdl_CreatesCreateTableStatement()
    {
        var service = new VirtualSchemaService();
        var schema = new VirtualSchema
        {
            Tables = new()
            {
                new VirtualTable
                {
                    Name = "users",
                    Columns = new()
                    {
                        new ColumnDefinition { Name = "id", DataType = "INTEGER", IsPrimaryKey = true },
                        new ColumnDefinition { Name = "name", DataType = "TEXT", IsNullable = true }
                    }
                }
            }
        };

        var ddl = service.GenerateDdl(schema, "SQLite");

        Assert.Contains("CREATE TABLE", ddl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("users", ddl);
        Assert.Contains("id", ddl);
    }

    [Fact]
    public void GenerateDdl_WithRelationship_IncludesForeignKey()
    {
        var service = new VirtualSchemaService();
        var schema = new VirtualSchema
        {
            Tables = new()
            {
                new VirtualTable { Name = "orders", Columns = new() { new ColumnDefinition { Name = "id", DataType = "INTEGER" } } },
                new VirtualTable { Name = "customers", Columns = new() { new ColumnDefinition { Name = "id", DataType = "INTEGER" } } }
            },
            Relationships = new()
            {
                new VirtualRelationship { FromTable = "orders", FromColumn = "customer_id", ToTable = "customers", ToColumn = "id" }
            }
        };

        var ddl = service.GenerateDdl(schema, "PostgreSQL");

        Assert.Contains("FOREIGN KEY", ddl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("customers", ddl);
    }

    [Fact]
    public async Task Load_WhenFileNotFound_ReturnsNull()
    {
        var service = new VirtualSchemaService();
        var path = CreateArtifactPath();

        DeleteIfExists(path);
        var result = await service.LoadAsync(path);

        Assert.Null(result);
    }

    private static string CreateArtifactPath()
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TestArtifacts"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{Guid.NewGuid():N}.dbcmodel");
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
