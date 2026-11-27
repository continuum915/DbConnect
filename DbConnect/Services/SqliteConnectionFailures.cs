using System;
using Microsoft.Data.Sqlite;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Maps Microsoft.Data.Sqlite exceptions from a connection test to a <see cref="ConnectionFailureCategory"/>.</summary>
public static class SqliteConnectionFailures
{
    private const int SqliteCantOpen = 14;
    private const int SqliteAuth = 23;

    public static ConnectionFailureCategory? Classify(Exception exception) => exception switch
    {
        // The file is missing (and the mode forbids creating it), or its directory is.
        SqliteException { SqliteErrorCode: SqliteCantOpen } => ConnectionFailureCategory.DatabaseNotFound,
        SqliteException { SqliteErrorCode: SqliteAuth } => ConnectionFailureCategory.Authentication,
        _ => null
    };
}
