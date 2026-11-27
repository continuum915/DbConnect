using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Owns the execution boundary; cleanup never substitutes for commit or rollback evidence.</summary>
public static class TransactionExecution
{
    public static async Task<TransactionResult> OpenAndExecuteAsync(
        Func<DbConnection> createConnection, IReadOnlyList<SqlCommandSpec> commands,
        bool namedParameters = true, Action<DbParameter>? configureParameter = null)
    {
        if (commands.Count == 0)
            return new TransactionResult { Succeeded = true, Outcome = TransactionOutcome.NoChanges };
        DbConnection? connection = null;
        TransactionResult result;
        try
        {
            connection = createConnection();
            await connection.OpenAsync();
        }
        catch (Exception ex)
        {
            result = new TransactionResult { Outcome = TransactionOutcome.NoChanges, ErrorMessage = ex.Message };
            if (connection != null)
                await DisposeConnectionAsync(connection, result);
            return result;
        }

        result = await ExecuteAsync(connection, null, commands, namedParameters, configureParameter);
        await DisposeConnectionAsync(connection, result);
        return result;
    }

    private static async Task DisposeConnectionAsync(DbConnection connection, TransactionResult result)
    {
        try
        {
            await connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            AddBoundaryError(result, "Connection disposal", ex);
        }
    }

    /// <summary>A supplied transaction belongs to the caller and is never ended or disposed here.</summary>
    public static async Task<TransactionResult> ExecuteAsync(
        DbConnection connection, DbTransaction? callerTransaction, IReadOnlyList<SqlCommandSpec> commands,
        bool namedParameters = true, Action<DbParameter>? configureParameter = null)
    {
        if (commands.Count == 0)
            return new TransactionResult { Succeeded = true, Outcome = TransactionOutcome.NoChanges };

        var transaction = callerTransaction;
        try
        {
            transaction ??= await connection.BeginTransactionAsync();
        }
        catch (Exception ex)
        {
            return new TransactionResult { Outcome = TransactionOutcome.NoChanges, ErrorMessage = ex.Message };
        }

        var ownsTransaction = callerTransaction == null;
        var affected = 0;
        var commandCounts = new Dictionary<SqlCommandSpec, int>();
        var cleanupErrors = new List<string>();
        TransactionResult? failure = null;
        foreach (var command in commands)
        {
            if (string.IsNullOrWhiteSpace(command.CommandText))
                continue;
            try
            {
                var count = await RunCommandAsync(connection, transaction, command, namedParameters, configureParameter, cleanupErrors);
                commandCounts[command] = count;
                affected += Math.Max(0, count);
                if (command.ExpectedRecordsAffected is int expected && count != expected)
                {
                    failure = TransactionResult.ConcurrencyConflict(command.CommandText, expected, count, affected, command);
                    break;
                }
            }
            catch (Exception ex)
            {
                failure = TransactionResult.Failure(command.CommandText, ex.Message, affected);
                break;
            }
        }

        var outcome = TransactionOutcome.Pending;
        string? boundaryError = null;
        if (ownsTransaction)
        {
            try
            {
                if (failure == null)
                {
                    await transaction.CommitAsync();
                    outcome = TransactionOutcome.Committed;
                }
                else
                {
                    await transaction.RollbackAsync();
                    outcome = TransactionOutcome.RolledBack;
                }
            }
            catch (Exception ex)
            {
                outcome = TransactionOutcome.Unknown;
                boundaryError = $"{(failure == null ? "Commit" : "Rollback")}: {ex.Message}";
            }
        }

        var result = new TransactionResult
        {
            Succeeded = failure == null && outcome != TransactionOutcome.Unknown,
            Outcome = outcome,
            RecordsAffected = outcome == TransactionOutcome.Committed ? affected : 0,
            AttemptedRecordsAffected = affected,
            ErrorMessage = failure?.ErrorMessage ?? boundaryError,
            BoundaryErrorMessage = boundaryError,
            FailedStatement = failure?.FailedStatement,
            FailedCommand = failure?.FailedCommand,
            IsConcurrencyConflict = failure?.IsConcurrencyConflict ?? false,
            ExpectedRecordsAffected = failure?.ExpectedRecordsAffected,
            ActualRecordsAffected = failure?.ActualRecordsAffected,
            CommandRecordsAffected = commandCounts
        };
        foreach (var error in cleanupErrors)
            result.BoundaryErrorMessage = JoinErrors(result.BoundaryErrorMessage, error);
        if (ownsTransaction)
        {
            try { await transaction.DisposeAsync(); }
            catch (Exception ex) { AddBoundaryError(result, "Transaction disposal", ex); }
        }
        return result;
    }

    private static async Task<int> RunCommandAsync(DbConnection connection, DbTransaction transaction,
        SqlCommandSpec command, bool namedParameters, Action<DbParameter>? configureParameter, List<string> cleanupErrors)
    {
        var cmd = connection.CreateCommand();
        try
        {
            cmd.Transaction = transaction;
            cmd.CommandText = command.CommandText;
            foreach (var parameter in command.Parameters)
            {
                var dbParameter = cmd.CreateParameter();
                if (namedParameters)
                    dbParameter.ParameterName = parameter.Key;
                dbParameter.Value = parameter.Value ?? DBNull.Value;
                configureParameter?.Invoke(dbParameter);
                cmd.Parameters.Add(dbParameter);
            }
            return await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            try { await cmd.DisposeAsync(); }
            catch (Exception ex) { cleanupErrors.Add($"Command disposal: {ex.Message}"); }
        }
    }

    private static void AddBoundaryError(TransactionResult result, string boundary, Exception exception) =>
        result.BoundaryErrorMessage = JoinErrors(result.BoundaryErrorMessage, $"{boundary}: {exception.Message}");

    private static string JoinErrors(string? previous, string next) =>
        string.IsNullOrEmpty(previous) ? next : $"{previous}; {next}";
}
