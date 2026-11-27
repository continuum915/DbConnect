using System;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>Serializes session opens and whole page refreshes independently for one table tab.</summary>
internal sealed class TableDataOperationCoordinator
{
    private readonly SemaphoreSlim _sessionOpenGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public async Task<IDbSession> OpenSessionAsync(Func<Task<IDbSession>> open, CancellationToken cancellationToken)
    {
        await _sessionOpenGate.WaitAsync(cancellationToken);
        try { return await open(); }
        finally { _sessionOpenGate.Release(); }
    }

    public async Task RefreshAsync(Func<Task> refresh)
    {
        await _refreshGate.WaitAsync();
        try { await refresh(); }
        finally { _refreshGate.Release(); }
    }
}
