using System;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Opens a query or table session and prepares it from the connection's options: session settings the
/// driver cannot take as keywords, then the startup SQL. A failing statement closes the session and fails
/// the open, naming the statement. Read-only connections also refuse grid edits on the session.
/// </summary>
public static class SessionSetup
{
    public static async Task<IDbSession> OpenAsync(DatabaseSession session, CancellationToken cancellationToken = default)
    {
        var opened = await session.Provider.OpenSessionAsync(session.Connection, cancellationToken);
        var options = session.Connection.Options ?? new ConnectionOptions();
        try
        {
            var statements = ConnectionOptionDialects.For(session.ProviderName)?.SessionSetup(options, session.Capabilities.BatchDialect) ?? [];
            foreach (var statement in statements)
            {
                var result = await opened.ExecuteQueryAsync(statement, new QueryExecutionOptions { CancellationToken = cancellationToken });
                if (result.HasError)
                    throw new InvalidOperationException($"Session setup failed on \"{statement}\": {result.ErrorMessage ?? result.Error?.Summary}");
            }
            if (options.IsReadOnly && opened is ConnectionDbSession connection)
                connection.WriteRefusal = $"{session.Name} is read-only; edits were not saved.";
            return opened;
        }
        catch
        {
            await opened.DisposeAsync();
            throw;
        }
    }
}
