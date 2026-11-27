using System.Collections.ObjectModel;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>The Backup / Restore dialog follows the selected session's backup strategy (mysql-duckdb-backup/01).</summary>
public sealed class BackupRestoreDialogTests
{
    private static DatabaseSession Session(IDatabaseProvider provider, string name) =>
        new(new ConnectionInfo { Name = name, Provider = provider.ProviderName, ConnectionString = "Data Source=:memory:" }, provider);

    [Fact]
    public async Task TheDialogDescribesTheSelectedSessionsBackupAndRestoreAndWarnsAboutPgDumpOnlyForPostgreSql()
    {
        var sqlite = Session(new SqliteProvider(), "local");
        var mysql = Session(new MySqlProvider(), "shop");
        var postgres = Session(new PostgreSqlProvider(), "warehouse");
        var dialog = new BackupRestoreViewModel([sqlite, mysql, postgres]);
        await dialog.InitializeAsync();
        var pgDumpFound = await new BackupService().CheckPgDumpAvailableAsync();

        dialog.TargetSession = mysql;
        Assert.Contains("tables, data, views, routines and triggers", dialog.StrategyDescription);
        Assert.Contains("Users, grants and events are not included", dialog.StrategyDescription);
        Assert.Equal("", dialog.PgDumpWarning);
        Assert.True(dialog.CanCancel);
        Assert.Equal("A restore runs the script one statement at a time and undoes nothing, so a failed or cancelled restore " +
                     "leaves the database partly restored. MySQL also commits DDL as it runs.", dialog.RestoreNote);
        dialog.IsBackupMode = false;
        Assert.True(dialog.CanCancel);

        dialog.TargetSession = sqlite;
        Assert.Contains("SQLite's online backup", dialog.StrategyDescription);
        Assert.Equal("", dialog.RestoreNote);
        Assert.Equal("", dialog.PgDumpWarning);
        Assert.False(dialog.CanCancel);

        dialog.TargetSession = postgres;
        Assert.Contains("pg_dump", dialog.StrategyDescription);
        Assert.Equal(pgDumpFound, dialog.PgDumpWarning == "");
    }

    [Fact]
    public async Task ABackupThatIsNotSupportedIsLoggedAsFailedNotComplete()
    {
        var duckdb = Session(new DuckDbProvider(), "analytics");
        var dialog = new BackupRestoreViewModel([duckdb]) { TargetSession = duckdb, OutputPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bak") };

        Assert.Equal("Backup is not available for this provider.", dialog.StrategyDescription);
        await dialog.StartBackupCommand.ExecuteAsync(null);

        Assert.Equal(["❌ Backup failed: Backup is not supported for provider 'DuckDB'."], dialog.LogLines);
        Assert.False(dialog.IsRunning);
    }
}
