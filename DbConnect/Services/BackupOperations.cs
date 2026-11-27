using System;
using System.Threading;
using System.Threading.Tasks;

namespace DbConnect.Services;

/// <summary>
/// Dispatches workflows using the provider's declared backup mechanism. A provider without one fails with
/// <see cref="NotSupportedException"/>, so neither the dialog nor a job can report it as done.
/// </summary>
public static class BackupOperations
{
    public static Task BackupAsync(this BackupService service, DatabaseSession session, string path, IProgress<string> progress,
        CancellationToken cancellationToken = default) =>
        session.Capabilities.BackupStrategy switch
        {
            BackupStrategy.SqliteOnline => service.BackupSqliteAsync(session.ConnectionString, path, progress),
            BackupStrategy.PostgreSqlTools => service.BackupPostgreSqlAsync(session.ConnectionString, path, progress),
            BackupStrategy.SqlServerCommands => service.BackupSqlServerAsync(session, path, progress),
            BackupStrategy.DbConnectDump => service.BackupWithDumpAsync(session, path, progress, cancellationToken),
            _ => Unsupported("Backup", session)
        };

    public static Task RestoreAsync(this BackupService service, DatabaseSession session, string path, IProgress<string> progress,
        CancellationToken cancellationToken = default) =>
        session.Capabilities.BackupStrategy switch
        {
            BackupStrategy.DbConnectDump => service.RestoreWithScriptAsync(session, path, progress, cancellationToken),
            BackupStrategy.PostgreSqlTools => service.RestorePostgreSqlAsync(session.ConnectionString, path, progress),
            BackupStrategy.SqlServerCommands => service.RestoreSqlServerAsync(session, path, progress),
            _ => Unsupported("Restore", session)
        };

    private static Task Unsupported(string operation, DatabaseSession session) =>
        Task.FromException(new NotSupportedException($"{operation} is not supported for provider '{session.ProviderName}'."));
}
