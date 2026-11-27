using System;
using System.Collections.Generic;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// A connection the <see cref="ConnectionEstablisher"/> made usable. <see cref="Settings"/> are the effective
/// settings (secrets resolved, endpoint rewritten, TLS applied) that the session, the metadata calls and the
/// provider see. Disposing releases everything the connection owns.
/// </summary>
public sealed class EstablishedConnection : IDisposable
{
    private readonly IReadOnlyList<IDisposable> _owned;

    internal EstablishedConnection(DatabaseSession session, IReadOnlyList<SchemaNode> schema, IReadOnlyList<IDisposable> owned)
    {
        Session = session;
        Schema = schema;
        _owned = owned;
    }

    public ConnectionInfo Settings => Session.Connection;
    public DatabaseSession Session { get; }
    public IReadOnlyList<SchemaNode> Schema { get; }
    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        if (IsDisposed)
            return;
        IsDisposed = true;
        Session.Catalog.Dispose();
        for (var i = _owned.Count - 1; i >= 0; i--)
            _owned[i].Dispose();
    }
}
