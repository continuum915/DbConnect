using System;
using Npgsql;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Maps Npgsql exceptions from a connection test to a <see cref="ConnectionFailureCategory"/>.</summary>
public static class PostgreSqlConnectionFailures
{
    public static ConnectionFailureCategory? Classify(Exception exception) => exception switch
    {
        // 28P01 invalid_password, 28000 invalid_authorization_specification (e.g. pg_hba rejects the user).
        PostgresException { SqlState: "28P01" or "28000" } => ConnectionFailureCategory.Authentication,
        PostgresException { SqlState: "3D000" } => ConnectionFailureCategory.DatabaseNotFound,
        _ => null
    };
}
