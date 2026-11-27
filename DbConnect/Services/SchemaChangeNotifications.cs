using System;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Connection-generation metadata notifications at existing execution completion boundaries.</summary>
public sealed class SchemaChangeNotifications(DatabaseSession session)
{
    public event Func<MetadataScope, Task>? Changed;

    public async Task<T> ExecuteAsync<T>(string sql, Func<Task<T>> execute, MetadataScope? affectedScope = null)
    {
        if (!SchemaChangeExecution.MayChangeSchema(sql, session.Capabilities.BatchDialect)) return await execute();
        try { return await execute(); }
        finally { await InvalidateAsync(affectedScope ?? MetadataScope.Root); }
    }

    public async Task InvalidateAsync(MetadataScope scope)
    {
        if (session.Catalog.IsDisposed) return;
        session.Catalog.Invalidate(scope);
        await NotifyAsync(scope);
    }

    public async Task PendingAsync(IDbSession owner)
    {
        if (session.Catalog.IsDisposed) return;
        session.Catalog.MarkPending(MetadataScope.Root, owner);
        await session.Catalog.PublishRefreshAsync(MetadataScope.Root);
        await NotifyAsync(MetadataScope.Root);
    }

    public async Task TransactionEndedAsync(IDbSession owner)
    {
        if (!owner.IsInTransaction && session.Catalog.EndPending(owner))
            await NotifyAsync(MetadataScope.Root);
    }

    private async Task NotifyAsync(MetadataScope scope)
    {
        if (Changed == null) return;
        foreach (Func<MetadataScope, Task> refresh in Changed.GetInvocationList()) await refresh(scope);
    }
}
