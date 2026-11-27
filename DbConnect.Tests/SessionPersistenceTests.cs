using System;
using System.IO;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public class SessionPersistenceTests
{
    [Fact]
    public async Task SaveSession_QueryTabs_SerializesQueryText()
    {
        var dir = CreateTestDirectory();
        try
        {
            var svc = new SessionPersistenceService(dir);
            var session = new SavedSession
            {
                WasCleanShutdown = true,
                Tabs = new() { new SavedTab { TabType = "Query", QueryText = "SELECT 1", Title = "Q1" } }
            };

            await svc.SaveAsync(session);
            var loaded = await svc.LoadAsync();

            Assert.NotNull(loaded);
            Assert.Single(loaded!.Tabs);
            Assert.Equal("SELECT 1", loaded.Tabs[0].QueryText);
        }
        finally
        {
            DeleteTestDirectory(dir);
        }
    }

    [Fact]
    public async Task SaveSession_NoSession_SkipsConnection()
    {
        var dir = CreateTestDirectory();
        try
        {
            var svc = new SessionPersistenceService(dir);
            var session = new SavedSession
            {
                WasCleanShutdown = true,
                Tabs = new() { new SavedTab { TabType = "Query", Title = "Q1", ConnectionName = null } }
            };

            await svc.SaveAsync(session);
            var loaded = await svc.LoadAsync();

            Assert.Null(loaded!.Tabs[0].ConnectionName);
        }
        finally
        {
            DeleteTestDirectory(dir);
        }
    }

    [Fact]
    public async Task RestoreSession_CleanShutdown_WasCleanShutdownIsTrue()
    {
        var dir = CreateTestDirectory();
        try
        {
            var svc = new SessionPersistenceService(dir);
            await svc.SaveAsync(new SavedSession { WasCleanShutdown = true });

            var loaded = await svc.LoadAsync();

            Assert.True(loaded!.WasCleanShutdown);
        }
        finally
        {
            DeleteTestDirectory(dir);
        }
    }

    [Fact]
    public async Task RestoreSession_CrashFile_WasCleanShutdownIsFalse()
    {
        var dir = CreateTestDirectory();
        try
        {
            var svc = new SessionPersistenceService(dir);
            await svc.SaveAsync(new SavedSession { WasCleanShutdown = false });

            var loaded = await svc.LoadAsync();

            Assert.False(loaded!.WasCleanShutdown);
        }
        finally
        {
            DeleteTestDirectory(dir);
        }
    }

    [Fact]
    public async Task LoadAsync_WhenNoFile_ReturnsNull()
    {
        var dir = CreateTestDirectory();
        try
        {
            var svc = new SessionPersistenceService(dir);
            var result = await svc.LoadAsync();

            Assert.Null(result);
        }
        finally
        {
            DeleteTestDirectory(dir);
        }
    }

    private static string CreateTestDirectory()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "SessionPersistenceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteTestDirectory(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }
}
