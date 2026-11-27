using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace DbConnect.Services;

/// <summary>
/// A connection a provider borrows for one statement: the session's own
/// connection when one is supplied (left open on dispose), otherwise a fresh
/// connection that is opened here and disposed with the lease.
/// </summary>
public readonly struct ConnectionLease<TConnection> : IAsyncDisposable
    where TConnection : DbConnection
{
    private readonly bool _owned;

    private ConnectionLease(TConnection connection, bool owned)
    {
        Connection = connection;
        _owned = owned;
    }

    public TConnection Connection { get; }

    /// <param name="shared">The session connection, or null to open a fresh one with <paramref name="create"/>.</param>
    public static async Task<ConnectionLease<TConnection>> AcquireAsync(
        TConnection? shared,
        Func<TConnection> create,
        CancellationToken cancellationToken)
    {
        if (shared != null)
            return new ConnectionLease<TConnection>(shared, owned: false);

        var connection = create();
        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }

        return new ConnectionLease<TConnection>(connection, owned: true);
    }

    public ValueTask DisposeAsync() =>
        _owned ? Connection.DisposeAsync() : ValueTask.CompletedTask;
}
