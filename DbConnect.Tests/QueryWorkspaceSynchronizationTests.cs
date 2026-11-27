using System.Threading;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.Tests;

public sealed class QueryWorkspaceSynchronizationTests
{
    [Fact]
    public async Task LoadingWithoutPumpingTheCallingContextDoesNotBlockOtherWorkspaceOperations()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rain-workspace-context-{Guid.NewGuid():N}.json");
        var workspace = new QueryWorkspaceService(path);
        await workspace.SaveQueryAsync(new SavedQueryDefinition { Name = "Large", Sql = new string('x', 1024 * 1024) });
        var context = new PausedContext();
        var started = new TaskCompletionSource<Task<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Task.Run(() =>
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(context);
            try { started.SetResult(workspace.LoadTimeoutSecondsAsync()); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        });
        var load = await started.Task;
        var other = new QueryWorkspaceService(path).SaveResultRowCapAsync(500);
        try
        {
            await other.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(30, await load.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(500, await workspace.LoadResultRowCapAsync());
        }
        finally
        {
            context.Resume();
            await Task.WhenAll(load, other).WaitAsync(TimeSpan.FromSeconds(10));
            File.Delete(path);
        }
    }

    private sealed class PausedContext : SynchronizationContext
    {
        private readonly List<(SendOrPostCallback Callback, object? State)> _pending = [];
        private bool _resumed;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (_pending)
            {
                if (!_resumed)
                {
                    _pending.Add((callback, state));
                    return;
                }
            }
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }

        public void Resume()
        {
            lock (_pending)
            {
                _resumed = true;
                foreach (var (callback, state) in _pending)
                    ThreadPool.QueueUserWorkItem(_ => callback(state));
                _pending.Clear();
            }
        }
    }
}
