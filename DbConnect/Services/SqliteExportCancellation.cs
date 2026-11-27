using System;
using System.Threading;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace DbConnect.Services;

/// <summary>SqliteCommand.Cancel is a no-op; poll cancellation during native VM execution.</summary>
internal sealed class SqliteExportCancellation : IDisposable
{
    private readonly sqlite3 _handle;

    public SqliteExportCancellation(SqliteConnection connection, CancellationToken cancellationToken)
    {
        _handle = connection.Handle!;
        // The session gate owns this connection until reader disposal and handler removal.
        raw.sqlite3_progress_handler(_handle, 1000, _ => cancellationToken.IsCancellationRequested ? 1 : 0, null);
    }

    public void Dispose() => raw.sqlite3_progress_handler(_handle, 0, null, null);
}
