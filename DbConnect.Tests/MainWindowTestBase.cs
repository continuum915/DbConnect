using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>Closes test-owned view models during xUnit asynchronous fixture teardown.</summary>
public abstract class MainWindowTestBase : IAsyncLifetime
{
    private readonly List<MainWindowViewModel> _viewModels = [];

    protected MainWindowViewModel CreateMainWindowViewModel(DatabaseService? service = null)
    {
        var viewModel = new MainWindowViewModel(service ?? new DatabaseService());
        _viewModels.Add(viewModel);
        return viewModel;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var failures = new List<Exception>();
        foreach (var viewModel in _viewModels)
        {
            try { await viewModel.OnCleanExitAsync(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (failures.Count > 0)
            throw new AggregateException("Test view-model cleanup failed.", failures);
    }
}
