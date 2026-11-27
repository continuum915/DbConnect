using System;
using System.IO;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using Xunit;

namespace DbConnect.Tests;

public class DiagramWorkspaceTests
{
    private static string CreateWorkspaceDir()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "diagram-workspace-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task SaveAndLoad_PreservesTableLayout()
    {
        var dir = CreateWorkspaceDir();
        try
        {
            var svc = new DiagramWorkspaceService(dir);
            var workspace = new DiagramWorkspace
            {
                ConnectionName = "mydb",
                DatabaseName = "testdb",
                TableLayouts = new() { new TableNodeLayout { TableName = "orders", X = 100, Y = 200 } }
            };

            await svc.SaveAsync(workspace);
            var loaded = await svc.LoadAsync("mydb", "testdb");

            Assert.NotNull(loaded);
            Assert.Single(loaded!.TableLayouts);
            Assert.Equal("orders", loaded.TableLayouts[0].TableName);
            Assert.Equal(100, loaded.TableLayouts[0].X);
            Assert.Equal(200, loaded.TableLayouts[0].Y);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task SaveAndLoad_PreservesHiddenTables()
    {
        var dir = CreateWorkspaceDir();
        try
        {
            var svc = new DiagramWorkspaceService(dir);
            var workspace = new DiagramWorkspace
            {
                ConnectionName = "c1",
                DatabaseName = "d1",
                HiddenTables = new() { "orders", "products" }
            };

            await svc.SaveAsync(workspace);
            var loaded = await svc.LoadAsync("c1", "d1");

            Assert.NotNull(loaded);
            Assert.Contains("orders", loaded!.HiddenTables);
            Assert.Contains("products", loaded.HiddenTables);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task SaveAndLoad_PreservesNotes()
    {
        var dir = CreateWorkspaceDir();
        try
        {
            var svc = new DiagramWorkspaceService(dir);
            var workspace = new DiagramWorkspace
            {
                ConnectionName = "c1",
                DatabaseName = "d1",
                Notes = new() { new DiagramNote { Text = "Hello", X = 50, Y = 75 } }
            };

            await svc.SaveAsync(workspace);
            var loaded = await svc.LoadAsync("c1", "d1");

            Assert.NotNull(loaded);
            Assert.Single(loaded!.Notes);
            Assert.Equal("Hello", loaded.Notes[0].Text);
            Assert.Equal(50, loaded.Notes[0].X);
            Assert.Equal(75, loaded.Notes[0].Y);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Load_WhenNoFile_ReturnsNull()
    {
        var dir = CreateWorkspaceDir();
        try
        {
            var svc = new DiagramWorkspaceService(dir);
            var result = await svc.LoadAsync("nonexistent", "db");
            Assert.Null(result);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DiagramWorkspace_ColorGroups_HasSixGroups()
    {
        Assert.Equal(6, ErDiagramViewModel.ColorGroups.Count);
    }

    [Fact]
    public async Task SaveWorkspace_SetsTimestamp()
    {
        var dir = CreateWorkspaceDir();
        try
        {
            var svc = new DiagramWorkspaceService(dir);
            var workspace = new DiagramWorkspace { ConnectionName = "a", DatabaseName = "b" };

            await svc.SaveAsync(workspace);

            Assert.False(string.IsNullOrEmpty(workspace.SavedAt));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void HideTable_RemovesFromVisibleTables()
    {
        var vm = new ErDiagramViewModel
        {
            Model = new ErDiagramModel
            {
                Tables =
                [
                    new ErTable { TableName = "orders" },
                    new ErTable { TableName = "products" }
                ]
            }
        };

        vm.HideTable("orders");

        Assert.Single(vm.VisibleTables);
        Assert.Equal("products", vm.VisibleTables[0].TableName);
    }

    [Fact]
    public void ShowTable_RestoresVisibleTables()
    {
        var vm = new ErDiagramViewModel
        {
            Model = new ErDiagramModel
            {
                Tables =
                [
                    new ErTable { TableName = "orders" },
                    new ErTable { TableName = "products" }
                ]
            }
        };

        vm.HideTable("orders");
        vm.ShowTable("orders");

        Assert.Equal(2, vm.VisibleTables.Count);
    }

    [Fact]
    public void AddNote_AppearsInNotesCollection()
    {
        var vm = new ErDiagramViewModel();

        vm.AddNote();

        Assert.Single(vm.Notes);
        Assert.Equal("Note", vm.Notes[0].Text);
    }
}
