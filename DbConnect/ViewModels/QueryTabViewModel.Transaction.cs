using System;
using System.Threading.Tasks;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class QueryTabViewModel
{
    private DatabaseSession? _metadataSessionOwner;

    /// <summary>One row at a time as name/value pairs (Form View).</summary>
    public RecordViewModel Record => field ??= new(new QueryRecordSource(this));

    /// <summary>Fetches more rows of a capped result from its still-open reader.</summary>
    public ResultPagingViewModel Paging => field ??= new(this, () => RebuildDisplayedResults(CurrentResult));

    /// <summary>Switches the tab between the databases of a server connection.</summary>
    public TabDatabasePicker DatabasePicker => field ??= new(this);

    /// <summary>Shown when the server or network dropped this tab's session.</summary>
    public ConnectionLossViewModel ConnectionLoss => field ??= new(async () =>
    {
        await ReleaseDbSessionAsync();
        await GetOrOpenDbSessionAsync();
        RefreshTransactionState();
    });

    /// <summary>Closes the tab's session, rolling back any open transaction. Safe to call repeatedly.</summary>
    public async Task ReleaseDbSessionAsync()
    {
        IDbSession? session;
        DatabaseSession? owner;
        lock (_dbSessionLock)
        {
            _dbSessionGeneration++;
            session = _dbSession;
            _dbSession = null;
            owner = _metadataSessionOwner;
            _metadataSessionOwner = null;
        }

        if (session == null) return;
        IsInTransaction = false;
        try
        {
            try { await session.DisposeAsync(); }
            finally
            {
                if (owner != null) await owner.SchemaChanges.TransactionEndedAsync(session);
            }
        }
        catch
        {
            // Releasing a session must never block closing a tab.
        }
    }

    /// <summary>The toolbar's Auto-commit box: the inverse of <see cref="IsManualCommit"/>.</summary>
    public bool AutoCommit
    {
        get => !IsManualCommit;
        set => IsManualCommit = !value;
    }

    public string TransactionStatusText => !IsInTransaction ? string.Empty :
        SelectedSession?.Catalog.GetSnapshot(MetadataScope.Root).State == MetadataLoadState.Pending
            ? "Transaction open · metadata changes pending until commit or rollback"
            : "Transaction open · uncommitted changes";

    private async Task EndTransactionAsync(bool commit)
    {
        var session = _dbSession;
        var owner = _metadataSessionOwner;
        if (IsExecuting || session is not { IsInTransaction: true })
        {
            RefreshTransactionState();
            return;
        }

        try
        {
            if (commit) await session.CommitAsync();
            else await session.RollbackAsync();
        }
        catch (Exception ex)
        {
            var failed = new QueryResult
            {
                Error = QueryErrorInfo.Failure(session.Provider.ProviderName, commit ? "Commit failed." : "Rollback failed.", ex.Message)
            };
            SetExecutionResults([failed], failed);
        }
        finally
        {
            RefreshTransactionState();
            if (owner != null) await owner.SchemaChanges.TransactionEndedAsync(session);
        }
    }
}
