using Npgsql;
using DbConnect.Services;

namespace DbConnect.Tests;

public class PostgreSqlBackupStartInfoTests
{
    [Theory]
    [InlineData("db host", "db user", "db name", "backup files/archive.dump")]
    [InlineData("host\"name", "user\"name", "db\"name", "archive\"name.dump")]
    [InlineData("--help", "--version", "--clean", "--verbose")]
    [InlineData("host --help", "user --version", "db --clean", "archive --verbose")]
    public void PreservesValuesAsSingleArgumentsForBackupAndRestore(
        string host, string username, string database, string path)
    {
        var connection = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = 5544,
            Username = username,
            Database = database,
            Password = "secret password"
        }.ConnectionString;

        var backup = PostgreSqlBackupStartInfo.CreateBackup(connection, path);
        var restore = PostgreSqlBackupStartInfo.CreateRestore(connection, path);

        Assert.Equal("pg_dump", backup.FileName);
        Assert.Equal("pg_restore", restore.FileName);
        Assert.Empty(backup.Arguments);
        Assert.Empty(restore.Arguments);
        Assert.Equal(new[]
        {
            $"--host={host}", "--port=5544", $"--username={username}",
            $"--dbname={database}", "--format=custom", $"--file={path}"
        }, backup.ArgumentList);
        Assert.Equal(new[]
        {
            $"--host={host}", "--port=5544", $"--username={username}",
            $"--dbname={database}", "--", path
        }, restore.ArgumentList);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeepsPasswordOnlyInEnvironmentAndDisablesShell(bool restore)
    {
        const string password = "secret \" --help; password";
        var connection = new NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Username = "tester",
            Database = "sample",
            Password = password
        }.ConnectionString;

        var info = restore
            ? PostgreSqlBackupStartInfo.CreateRestore(connection, "archive.dump")
            : PostgreSqlBackupStartInfo.CreateBackup(connection, "archive.dump");

        Assert.Equal(password, info.Environment["PGPASSWORD"]);
        Assert.Empty(info.Arguments);
        Assert.DoesNotContain(info.ArgumentList, argument => argument.Contains(password));
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.True(info.RedirectStandardError);
        Assert.True(info.RedirectStandardOutput);
    }
}
