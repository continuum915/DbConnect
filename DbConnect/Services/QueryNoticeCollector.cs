using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbConnect.Services;

/// <summary>Collects notices emitted while one command is executing on a possibly shared connection.</summary>
internal sealed class QueryNoticeCollector : IDisposable
{
    private readonly NpgsqlConnection? _postgres;
    private readonly SqlConnection? _sqlServer;
    private readonly NoticeEventHandler? _postgresHandler;
    private readonly SqlInfoMessageEventHandler? _sqlServerHandler;

    public QueryNoticeCollector(NpgsqlConnection connection, List<string> messages)
    {
        _postgres = connection;
        Messages = messages;
        _postgresHandler = (_, args) => Messages.Add($"[{args.Notice.Severity}] {args.Notice.MessageText}");
        connection.Notice += _postgresHandler;
    }

    public QueryNoticeCollector(SqlConnection connection, List<string> messages)
    {
        _sqlServer = connection;
        Messages = messages;
        _sqlServerHandler = (_, args) => Messages.Add(args.Message);
        connection.InfoMessage += _sqlServerHandler;
    }

    public List<string> Messages { get; }

    public void Dispose()
    {
        if (_postgres != null)
            _postgres.Notice -= _postgresHandler;
        if (_sqlServer != null)
            _sqlServer.InfoMessage -= _sqlServerHandler;
    }
}
