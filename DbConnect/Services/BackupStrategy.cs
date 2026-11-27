namespace DbConnect.Services;

/// <summary>The mechanism used by the backup module.</summary>
public enum BackupStrategy
{
    None,
    SqliteOnline,
    PostgreSqlTools,
    SqlServerCommands,
    /// <summary>DbConnect's own Dump SQL File: a portable script of the whole database, restored by running it.</summary>
    DbConnectDump
}

public static class BackupStrategyDescriptions
{
    /// <summary>What a backup made with <paramref name="strategy"/> is and holds, for the Backup / Restore dialog.</summary>
    public static string Describe(this BackupStrategy strategy) => strategy switch
    {
        BackupStrategy.SqliteOnline => "A copy of the database file, made with SQLite's online backup.",
        BackupStrategy.PostgreSqlTools => "A pg_dump archive of the database, restored with pg_restore. Needs pg_dump and pg_restore on PATH.",
        BackupStrategy.SqlServerCommands =>
            "BACKUP DATABASE to a .bak file. The path is on the SQL Server machine, not this computer.",
        BackupStrategy.DbConnectDump =>
            "A SQL script written by DbConnect's Dump SQL File, read in one snapshot: tables, data, views, routines and triggers. " +
            "Users, grants and events are not included. No client tools are needed.",
        _ => "Backup is not available for this provider."
    };

    /// <summary>
    /// What a restore into <paramref name="session"/> with <paramref name="strategy"/> leaves when it fails or is
    /// cancelled, where that is not obvious; empty otherwise.
    /// </summary>
    public static string RestoreNote(this BackupStrategy strategy, DatabaseSession session) => strategy switch
    {
        BackupStrategy.DbConnectDump =>
            "A restore runs the script one statement at a time and undoes nothing, so a failed or cancelled restore " +
            "leaves the database partly restored." +
            (session.Capabilities.TransactionalDdl ? "" : $" {session.ProviderName} also commits DDL as it runs."),
        _ => ""
    };

    /// <summary>Whether a backup or restore with <paramref name="strategy"/> stops when cancelled; the others run to the end.</summary>
    public static bool CanCancel(this BackupStrategy strategy) => strategy == BackupStrategy.DbConnectDump;
}
