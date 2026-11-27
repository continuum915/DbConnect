using System;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;

namespace DbConnect.ViewModels;

public sealed class QueryMessageRow
{
    private static readonly char[] LineBreaks = ['\r', '\n', '\t'];

    private readonly QueryStatementExecution _statement;

    public QueryMessageRow(QueryStatementExecution statement, Action navigateError)
    {
        _statement = statement;
        NavigateErrorCommand = new RelayCommand(navigateError);
    }

    public int StatementNumber => _statement.Index + 1;

    /// <summary>The statement's SQL on one line, shortened, for the Messages grid.</summary>
    public string Statement
    {
        get
        {
            var sql = _statement.Sql;
            var preview = sql.Length > 400 ? sql[..400] : sql;
            var line = string.Join(' ', preview.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries)).Trim();
            return line.Length > 200 ? line[..200] + "…" : line;
        }
    }

    /// <summary>"OK", or the error for a failed statement.</summary>
    public string Result => HasError ? Error : "OK";
    public string Rows => _statement.HasResultSet
        ? $"{_statement.RowCount:N0} returned"
        : _statement.RecordsAffected >= 0
            ? $"{_statement.RecordsAffected:N0} affected"
            : "—";
    public string Duration => $"{_statement.Duration.TotalMilliseconds:N0} ms";
    public string Warnings => string.Join(Environment.NewLine, _statement.Messages);
    public string Error => _statement.Error?.Summary ?? string.Empty;
    public bool HasError => _statement.Error != null;
    public IRelayCommand NavigateErrorCommand { get; }
}
