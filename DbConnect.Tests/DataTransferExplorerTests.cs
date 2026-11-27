using System.Collections.ObjectModel;
using Microsoft.Data.Sqlite;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>Explorer ▸ Transfer Data… and the database pickers for server connections (data-transfer/07).</summary>
public sealed class DataTransferExplorerTests : MainWindowTestBase, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rain-transfer-explorer-" + Guid.NewGuid().ToString("N"));
    public DataTransferExplorerTests() => Directory.CreateDirectory(_directory);
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    private DatabaseSession Sqlite(string name, string? setup = null)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, name + ".sqlite"), Pooling = false }.ToString();
        if (setup != null)
        {
            using var connection = new SqliteConnection(cs);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = setup;
            cmd.ExecuteNonQuery();
        }
        return new DatabaseSession(new ConnectionInfo { Name = name, Provider = "SQLite", ConnectionString = cs }, new SqliteProvider());
    }

    private const string Tables = "CREATE TABLE a (id INTEGER PRIMARY KEY); CREATE TABLE b (id INTEGER PRIMARY KEY); CREATE TABLE c (id INTEGER PRIMARY KEY); INSERT INTO b VALUES (1);";

    private (MainWindowViewModel Vm, SchemaNode Root, DatabaseSession Session) Explorer()
    {
        var vm = CreateMainWindowViewModel(new DatabaseService());
        var root = new SchemaNode { Name = "db", RawName = "db", Type = SchemaNodeType.Database };
        var session = Sqlite("db", Tables);
        vm.ActiveSessions[root] = session;
        foreach (var name in new[] { "a", "b", "c" })
            root.Children.Add(new SchemaNode { Name = name, RawName = name, Type = SchemaNodeType.Table, SchemaName = "main" });
        return (vm, root, session);
    }

    [Theory]
    [InlineData(SchemaNodeType.Database, true)]
    [InlineData(SchemaNodeType.Schema, true)]
    [InlineData(SchemaNodeType.Table, true)]
    [InlineData(SchemaNodeType.View, false)]
    [InlineData(SchemaNodeType.Column, false)]
    public void TransferDataIsOfferedOnConnectionDatabaseSchemaAndTableNodes(SchemaNodeType type, bool offered)
    {
        var (vm, root, _) = Explorer();
        var node = type == SchemaNodeType.Database ? root : new SchemaNode { Name = "x", RawName = "x", Type = type, SchemaName = "main" };
        if (node != root) root.Children.Add(node);

        var availability = vm.Explorer.Availability(node, ExplorerObjectAction.TransferData);

        Assert.Equal(offered, availability.IsVisible);
        Assert.Equal(offered, availability.IsEnabled);
    }

    [Fact]
    public async Task OnSelectedTablesTheWizardOpensWithThatSourceAndOnlyThoseTablesTicked()
    {
        var (vm, root, session) = Explorer();
        var target = Sqlite("target");

        vm.Explorer.TransferData(root.Children[1], [root.Children[1], root.Children[2]]);

        var wizard = Assert.IsType<DataTransferViewModel>(vm.SelectedTab);
        Assert.Same(session, wizard.Source);
        Assert.True(wizard.IsTargetStep);
        wizard.TargetSession = target;
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Equal(["main.b", "main.c"], wizard.Tables.Where(t => t.IsSelected).Select(t => t.Name));

        // Choosing another source by hand ticks every table again.
        wizard.Step = DataTransferStep.Source;
        wizard.SourceSession = Sqlite("other", Tables);
        await wizard.NextCommand.ExecuteAsync(null);
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.All(wizard.Tables, t => Assert.True(t.IsSelected));
    }

    [Fact]
    public async Task OnTheConnectionEveryTableIsTickedAndTheOpenWizardIsReused()
    {
        var (vm, root, _) = Explorer();
        vm.Explorer.TransferData(root.Children[0], []);
        var first = vm.SelectedTab;

        vm.Explorer.TransferData(root, []);

        var wizard = Assert.IsType<DataTransferViewModel>(vm.SelectedTab);
        Assert.Same(first, wizard);
        wizard.TargetSession = Sqlite("target");
        await wizard.NextCommand.ExecuteAsync(null);
        Assert.Equal(3, wizard.Tables.Count(t => t.IsSelected));
    }

    [Fact]
    public async Task OnAServerConnectionTheDatabasePickerChoosesTheDatabaseEachSideUses()
    {
        // A server whose databases are SQLite files: "db" is the connection's own, "archive" another one.
        var archive = Sqlite("archive", "CREATE TABLE old (id INTEGER PRIMARY KEY); INSERT INTO old VALUES (7);");
        var server = new ServerDatabaseDirectory(() => ["db", "archive"],
            name => Task.FromResult<DatabaseSession?>(name == "archive" ? archive : null), null);
        var connection = Sqlite("db", Tables);
        connection.Server = server;
        var target = Sqlite("target");
        var vm = new DataTransferViewModel(new ObservableCollection<DatabaseSession> { connection, target }) { SourceSession = connection };

        Assert.True(vm.SourceDatabase.IsAvailable);
        Assert.Equal(["db", "archive"], vm.SourceDatabase.Databases);
        Assert.Equal(vm.SourceSession.DatabaseLabel, vm.SourceDatabase.Selected);
        Assert.Same(connection, vm.Source);
        await vm.SourceDatabase.ChooseAsync("archive");
        Assert.Same(archive, vm.Source);
        await vm.SourceDatabase.ChooseAsync("missing");
        Assert.Equal("Database missing could not be opened.", vm.SourceDatabase.Notice);
        await vm.SourceDatabase.ChooseAsync("archive");

        await vm.NextCommand.ExecuteAsync(null);
        vm.TargetSession = target;
        Assert.False(vm.TargetDatabase.IsAvailable);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Equal(["main.old"], vm.Tables.Select(t => t.Name));
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);
        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal("Transferred 1 of 1 table · 1 rows.", vm.Summary);
    }
}
