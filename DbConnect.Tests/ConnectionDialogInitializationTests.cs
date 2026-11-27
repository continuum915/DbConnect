using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

public sealed class ConnectionDialogInitializationTests
{
    [Fact]
    public async Task SavingWhileSavedConnectionsLoadKeepsExistingConnections()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rain-dialog-load-{Guid.NewGuid():N}");
        var store = new PausedCredentialStore();
        var settings = new ConnectionSettingsService(store, directory);
        var existing = new ConnectionInfo { Name = "Existing", Provider = "SQLite", ConnectionString = "Data Source=existing.db" };
        await settings.SaveConnectionsAsync([existing]);
        var editor = new ConnectionDialogViewModel(new DatabaseService(), settings)
        {
            SelectedProvider = "SQLite",
            ConnectionName = "New",
            DatabaseFilePath = "new.db"
        };
        Task? save = null;
        try
        {
            await store.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            save = editor.SaveConnectionCommand.ExecuteAsync(null);
            Assert.False(save.IsCompleted, "Save must wait for the existing saved connections to finish loading.");
            store.Release.TrySetResult();
            await save.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(["Existing", "New"], (await settings.LoadConnectionsAsync()).Select(c => c.Name).Order().ToArray());
        }
        finally
        {
            store.Release.TrySetResult();
            if (save != null)
                await save.WaitAsync(TimeSpan.FromSeconds(10));
            await editor.Initialization.WaitAsync(TimeSpan.FromSeconds(10));
            Directory.Delete(directory, true);
        }
    }

    private sealed class PausedCredentialStore : ICredentialStore
    {
        public TaskCompletionSource LoadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string?> GetAsync(string key)
        {
            LoadStarted.TrySetResult();
            await Release.Task;
            return null;
        }

        public Task SetAsync(string key, string secret) => Task.CompletedTask;
        public Task DeleteAsync(string key) => Task.CompletedTask;
    }
}
