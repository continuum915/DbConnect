using System;
using MySqlConnector;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Maps MySqlConnector exceptions from a connection test to a <see cref="ConnectionFailureCategory"/>.</summary>
public static class MySqlConnectionFailures
{
    public static ConnectionFailureCategory? Classify(Exception exception) => exception switch
    {
        MySqlException { ErrorCode: MySqlErrorCode.AccessDenied } => ConnectionFailureCategory.Authentication,
        // 1044 means the password was accepted but the database can't be opened: MySQL reports a missing
        // database this way to users without global privileges, so it can't be told apart from a missing grant.
        MySqlException { ErrorCode: MySqlErrorCode.UnknownDatabase or MySqlErrorCode.DatabaseAccessDenied } =>
            ConnectionFailureCategory.DatabaseNotFound,
        MySqlException { ErrorCode: MySqlErrorCode.UnableToConnectToHost } => ConnectionFailureCategory.HostUnreachable,
        _ => null
    };
}
