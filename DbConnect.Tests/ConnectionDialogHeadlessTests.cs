using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbConnect.ViewModels;
using DbConnect.Views;
using Xunit;

namespace DbConnect.Tests;

/// <summary>
/// architecture-discipline ticket 01 — every provider the dialog offers must show fields to connect
/// with and must produce a connection string. The dialog picks its panels by provider name.
/// </summary>
public class ConnectionDialogHeadlessTests
{
    [AvaloniaFact]
    public void SwitchingProvidersShowsOnlySupportedCertificateFieldsAndModes()
    {
        var vm = new ConnectionDialogViewModel();
        var dialog = new ConnectionDialog { DataContext = vm };
        dialog.Show();
        // SSL settings are on the dialog's SSL tab (UI redesign 09).
        dialog.FindControl<TabControl>("ConnectionTabs")!.SelectedItem = dialog.FindControl<TabItem>("SslTab");
        foreach (var provider in new[] { "PostgreSQL", "MySQL", "SQL Server", "SQLite", "DuckDB", "PostgreSQL" })
        {
            vm.SelectedProvider = provider;
            Dispatcher.UIThread.RunJobs();
            var supported = provider is "PostgreSQL" or "MySQL";
            Assert.Equal(supported, dialog.FindControl<TextBox>("SslClientCertificate")!.IsEffectivelyVisible);
            Assert.Equal(supported, dialog.FindControl<TextBox>("SslCaCertificate")!.IsEffectivelyVisible);
            Assert.Equal(supported, dialog.FindControl<TextBox>("SslClientKey")!.IsEffectivelyVisible);
            if (supported) Assert.Contains("VerifyFull", vm.SslModes);
        }
        dialog.Close();
    }

    [AvaloniaTheory]
    [InlineData("SQLite")]
    [InlineData("PostgreSQL")]
    [InlineData("SQL Server")]
    [InlineData("MySQL")]
    [InlineData("DuckDB")]
    public void EveryOfferedProvider_ShowsConnectionFieldsAndBuildsAConnectionString(string provider)
    {
        var viewModel = new ConnectionDialogViewModel();
        Assert.Contains(provider, viewModel.AvailableProviders);

        viewModel.SelectedProvider = provider;
        viewModel.DatabaseFilePath = "C:/data/test.db";
        viewModel.Host = "db.example.com";
        viewModel.Database = "shop";
        viewModel.Username = "rain";

        var dialog = new ConnectionDialog { DataContext = viewModel };
        var shownInputs = dialog.GetLogicalDescendants().OfType<TextBox>()
            .Where(box => box.GetSelfAndLogicalAncestors().OfType<Control>().Where(control => control is not Window).All(control => control.IsVisible))
            .Select(box => box.Watermark?.ToString())
            .ToList();
        var fileField = shownInputs.Contains("/path/to/database.db");
        var hostField = shownInputs.Contains("localhost");

        var connectionString = (string)typeof(ConnectionDialogViewModel)
            .GetMethod("BuildConnectionString", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, null)!;

        Assert.True((fileField || hostField) && !string.IsNullOrEmpty(connectionString),
            $"{provider}: file or host field shown: {fileField || hostField} (visible inputs: {string.Join(", ", shownInputs)}); connection string: \"{connectionString}\".");
    }

    /// <summary>milestone-0 ticket 01 — a failed test names its cause; the driver message is one click away.</summary>
    [AvaloniaFact]
    public async Task FailedTest_ShowsCategoryAndSummary_WithDriverDetailOnDemand()
    {
        var viewModel = new ConnectionDialogViewModel
        {
            SelectedProvider = "SQLite",
            DatabaseFilePath = Path.Combine(Path.GetTempPath(), $"rain-missing-{Guid.NewGuid():N}", "test.sqlite")
        };
        var dialog = new ConnectionDialog { DataContext = viewModel };
        dialog.Show();

        await viewModel.TestConnectionCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var shown = string.Join(" | ", ShownText(dialog));
        Assert.Contains("Database not found", shown);
        Assert.Contains("The database does not exist, or this user may not open it.", shown);
        Assert.DoesNotContain("SqliteException", shown);

        var details = dialog.GetLogicalDescendants().OfType<Expander>().Single(expander => Equals(expander.Header, "Details"));
        Assert.True(details.IsEffectivelyVisible);
        details.IsExpanded = true;
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("SqliteException", string.Join(" | ", ShownText(dialog)));
        dialog.Close();
    }

    /// <summary>milestone-0 ticket 01 — a successful test shows the server reached and how long it took.</summary>
    [AvaloniaFact]
    public async Task SuccessfulTest_ShowsServerVersionAndElapsedTime()
    {
        var viewModel = new ConnectionDialogViewModel { SelectedProvider = "SQLite", DatabaseFilePath = ":memory:" };
        var dialog = new ConnectionDialog { DataContext = viewModel };
        dialog.Show();

        await viewModel.TestConnectionCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var shown = string.Join(" | ", ShownText(dialog));
        Assert.Matches(@"Connected to SQLite 3\.\d+\.\d+ in \d+ ms", shown);
        Assert.DoesNotContain(dialog.GetLogicalDescendants().OfType<Expander>(), expander => Equals(expander.Header, "Details") && expander.IsEffectivelyVisible);
        dialog.Close();
    }

    private static IEnumerable<string> ShownText(Window window) =>
        window.GetVisualDescendants().OfType<Control>()
            .Where(control => control.IsEffectivelyVisible)
            .Select(control => control switch
            {
                TextBlock block => block.Text,
                TextBox box => box.Text,
                _ => null
            })
            .OfType<string>()
            .Where(text => text.Length > 0);
}
