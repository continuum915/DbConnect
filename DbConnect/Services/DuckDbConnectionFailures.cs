using System;
using DuckDB.NET.Data;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Maps DuckDB.NET exceptions from a connection test to a <see cref="ConnectionFailureCategory"/>.</summary>
public static class DuckDbConnectionFailures
{
    public static ConnectionFailureCategory? Classify(Exception exception) => exception switch
    {
        // DuckDB raises a generic IO error for a missing file; the message is the only thing that tells it apart.
        DuckDBException duck when duck.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase) =>
            ConnectionFailureCategory.DatabaseNotFound,
        _ => null
    };
}
