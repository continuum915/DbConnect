using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DbConnect.Models;
using DbConnect.Tests.UiSnapshots;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests.Styles;

/// <summary>Ticket 04: the main-window shell keeps one sidebar width, a steady tree and a single-row tab strip.</summary>
public sealed class MainWindowShellTests : MainWindowTestBase
{
    [AvaloniaFact]
    public async Task SidebarSections_AreAsWideAsTheTree()
    {
        await using var shell = await Shell.ConnectAsync(() => CreateMainWindowViewModel());

        var tree = shell.Window.FindControl<TreeView>("SchemaTree")!;
        foreach (var name in new[] { "SnippetsSection" })
        {
            var section = shell.Window.FindControl<SidebarSection>(name)!;
            Assert.True(section.IsEffectivelyVisible, $"{name} is hidden");
            Assert.Equal(tree.Bounds.Width, section.Bounds.Width, 1.0);
        }
    }

    [AvaloniaFact]
    public async Task SelectingAndClearingANode_LeavesTheTreeHeightAlone()
    {
        await using var shell = await Shell.ConnectAsync(() => CreateMainWindowViewModel());
        var tree = shell.Window.FindControl<TreeView>("SchemaTree")!;
        var before = tree.Bounds.Height;

        shell.ViewModel.SelectedSchemaNode = shell.Table;
        shell.Pump();
        Assert.Equal(before, tree.Bounds.Height, 0.5);

        shell.ViewModel.SelectedSchemaNode = null;
        shell.Pump();
        Assert.Equal(before, tree.Bounds.Height, 0.5);
    }

    [AvaloniaFact]
    public async Task TreeRows_AreRowHeightTall()
    {
        await using var shell = await Shell.ConnectAsync(() => CreateMainWindowViewModel());
        var tree = shell.Window.FindControl<TreeView>("SchemaTree")!;
        var rowHeight = (double)shell.Window.FindResource("DbConnect.Size.RowHeight")!;

        var rows = tree.GetVisualDescendants().OfType<TreeViewItem>()
            .Where(item => item.IsEffectivelyVisible)
            .Select(item => item.GetVisualDescendants().OfType<Control>().First(c => c.Name == "PART_Header" || c.Name == "PART_LayoutRoot"))
            .ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Equal(rowHeight, row.Bounds.Height, 0.5));
    }

    [AvaloniaFact]
    public async Task TwentyTabs_StayOnOneRow_AndTheOverflowMenuListsThemAll()
    {
        await using var shell = await Shell.ConnectAsync(() => CreateMainWindowViewModel());
        for (var i = 0; i < 20; i++)
            shell.ViewModel.NewQueryTabCommand.Execute(null);
        shell.Pump();

        var strip = shell.Window.FindControl<TabControl>("MainTabControl")!;
        var headers = strip.GetRealizedContainers().OfType<TabItem>().ToList();
        Assert.True(shell.ViewModel.Tabs.Count >= 20);
        // The permanent Objects tab leads the strip, then one header per document.
        Assert.Equal(shell.ViewModel.Tabs.Count + 1, headers.Count);
        Assert.IsType<ObjectsTabViewModel>(headers[0].DataContext);
        var tops = headers.Select(h => h.TranslatePoint(default, strip)!.Value.Y).Distinct().ToList();
        Assert.Single(tops);

        var menu = TabOverflowMenu.Build(strip);
        Assert.Equal(shell.ViewModel.Tabs.Count + 1, menu.Count);
        menu[4].Command!.Execute(null);
        Assert.Same(shell.ViewModel.Tabs[3], shell.ViewModel.SelectedTab);
    }

    [AvaloniaFact]
    public async Task EveryDocumentTab_HasOneFluentIconAndAPlainTitle()
    {
        await using var shell = await Shell.ConnectAsync(() => CreateMainWindowViewModel());
        var vm = shell.ViewModel;
        vm.NewQueryTabCommand.Execute(null);
        vm.OpenTableDataTab(shell.Table);
        vm.Tools.OpenTableDesignerTab(shell.Table);
        vm.Tools.OpenIndexManagerTab(shell.Table);
        vm.Tools.OpenDependencyAnalysisTab(shell.Table);
        foreach (var name in UiSnapshotCapture.ToolCommands)
            Assert.True(await UiSnapshotCapture.OpenToolAsync(vm, name), name);
        Assert.True(await UiSnapshotCapture.OpenToolAsync(vm, "OpenMaskingRulesCommand"));
        shell.Pump();

        var missing = vm.Tabs.Where(tab => tab.TabIcon is null).Select(tab => tab.GetType().Name).ToList();
        Assert.True(missing.Count == 0, "No TabIcon: " + string.Join(", ", missing));
        var glyphs = vm.Tabs.Where(tab => tab.Title.EnumerateRunes().Any(r => r.Value > 0x2000 && r.Value != 0x2026 && r.Value != 0x2014))
            .Select(tab => tab.Title).ToList();
        Assert.True(glyphs.Count == 0, "Glyph in title: " + string.Join(", ", glyphs));
    }

    [AvaloniaFact]
    public async Task MainToolbarTiles_AreAtLeast58Square_AndTheCheckedTileFollowsTheObjectType()
    {
        await using var shell = await Shell.ConnectAsync(() => CreateMainWindowViewModel());
        var toolbar = shell.Window.FindControl<MainToolbarView>("MainToolbar")!;

        var tiles = toolbar.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("tile")).ToList();
        Assert.True(tiles.Count >= 14);
        Assert.All(tiles, tile => Assert.Equal(58, tile.Bounds.Height, 0.5));
        Assert.All(tiles, tile => Assert.True(tile.Bounds.Width >= 58 - 0.5, $"{tile.Name} is {tile.Bounds.Width} wide"));

        Button Tile(string name) => tiles.Single(t => AutomationProperties.GetName(t) == name);
        Assert.Contains("checked", Tile("Tables").Classes);

        shell.ViewModel.Frame.Toolbar.ShowObjectsCommand.Execute(ObjectBrowserKind.Views);
        shell.Pump();
        Assert.Contains("checked", Tile("Views").Classes);
        Assert.DoesNotContain("checked", Tile("Tables").Classes);

        shell.ViewModel.Frame.Toolbar.ShowObjectsCommand.Execute(ObjectBrowserKind.Sequences);
        shell.Pump();
        Assert.Contains("checked", Tile("Other object types").Classes);
        Assert.Equal(1, tiles.Count(t => t.Classes.Contains("checked")));
    }

    [AvaloniaFact]
    public async Task StatusBar_FollowsTheActiveTab()
    {
        await using var shell = await Shell.ConnectAsync(() => CreateMainWindowViewModel());
        var breadcrumb = shell.Window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "Breadcrumb");
        var connection = shell.Window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ConnectionText");

        shell.ViewModel.OpenTableDataTab(shell.Table);
        shell.Pump();
        var table = shell.ViewModel.SelectedTab!;
        Assert.EndsWith(table.Title, breadcrumb.Text);
        Assert.StartsWith("Shop · ", connection.Text);

        shell.ViewModel.NewQueryTabCommand.Execute(null);
        shell.Pump();
        Assert.EndsWith(shell.ViewModel.SelectedTab!.Title, breadcrumb.Text);
        Assert.NotEqual(table.Title, shell.ViewModel.SelectedTab!.Title);
        Assert.Equal($"{shell.ViewModel.SelectedTab!.Title} — DbConnect", shell.Window.Title);
    }

    [AvaloniaFact]
    public void WindowChromeTheming_IsAppliedToDialogsToo()
    {
        var themed = new List<Window>();
        void OnApplied(Window window, Avalonia.Styling.ThemeVariant variant) => themed.Add(window);
        WindowChromeTheming.Applied += OnApplied;
        try
        {
            var dialog = new AboutDialog();
            dialog.Show();
            HeadlessFrames.Pump(dialog);
            Assert.Contains(dialog, themed);
            dialog.Close();
        }
        finally
        {
            WindowChromeTheming.Applied -= OnApplied;
        }
    }

    [AvaloniaFact]
    public async Task Navigator_ShowsTheHeaderTheConnectionMetaAndColouredIcons()
    {
        await using var shell = await Shell.ConnectAsync(() => CreateMainWindowViewModel());
        var root = shell.ViewModel.SchemaNodes[0];
        await WaitUntilAsync(() => root.Meta.StartsWith("SQLite ", StringComparison.Ordinal));
        shell.Pump();

        var header = shell.Window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("pane-header"));
        Assert.Equal(29, header.Bounds.Height, 0.5);
        Assert.Contains(header.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Connections");
        Assert.NotNull(shell.Window.FindControl<Button>("CollapseAllButton"));

        var tree = shell.Window.FindControl<TreeView>("SchemaTree")!;
        var connectionRow = tree.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("node-label") && t.Text == root.Name);
        Assert.Equal(Avalonia.Media.FontWeight.SemiBold, connectionRow.FontWeight);
        var meta = tree.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("node-meta") && t.Text == root.Meta);
        Assert.True(meta.IsEffectivelyVisible);
        Assert.True(meta.Bounds.Right > connectionRow.Bounds.Right);

        var tableIcon = tree.GetVisualDescendants().OfType<FluentIcons.Avalonia.SymbolIcon>()
            .First(icon => icon.Classes.Contains("node-icon") && Equals(icon.Tag, "IconBlue"));
        Assert.Equal((Avalonia.Media.Color)shell.Window.FindResource(shell.Window.ActualThemeVariant, "DbConnect.Color.IconBlue")!,
            ThemedHost.ColorOf(tableIcon.Foreground));
    }

    [AvaloniaFact]
    public async Task CollapseAll_CollapsesEveryNode_AndSelectingATableSetsTheObjectsType()
    {
        await using var shell = await Shell.ConnectAsync(() => CreateMainWindowViewModel());
        shell.ViewModel.Frame.Toolbar.ObjectType = ObjectBrowserKind.Views;
        shell.ViewModel.SelectedSchemaNode = shell.Table;
        Assert.Equal(ObjectBrowserKind.Tables, shell.ViewModel.Frame.Toolbar.ObjectType);

        shell.ViewModel.SchemaTree.CollapseAllCommand.Execute(null);
        shell.Pump();
        Assert.All(shell.ViewModel.SchemaNodes, node => Assert.False(node.IsExpanded));
    }

    [AvaloniaFact]
    public void TableContextMenu_KeepsEveryCommand()
    {
        var window = new MainWindow { DataContext = CreateMainWindowViewModel() };
        try
        {
            window.Show();
            var menu = window.FindControl<TreeView>("SchemaTree")!.ContextMenu!;
            var names = menu.Items.OfType<MenuItem>().Select(item => item.Name).ToHashSet();
            string[] expected =
            [
                "CtxOpenData", "CtxDesignTable", "CtxCreateTable", "CtxNewQueryTab", "CtxCopyName", "CtxCopyDdl", "CtxScriptAs",
                "CtxExportData", "CtxImportCsvIntoTable", "CtxImportCsv", "CtxDumpSql", "CtxTransferData", "CtxFindInData",
                "CtxViewDependencies", "CtxCompareData", "CtxManageIndexes", "CtxErDiagram", "CtxCreateView", "CtxEditView",
                "CtxCreateRoutine", "CtxBackupRestore", "CtxDuplicateTable", "CtxRenameObject", "CtxEmptyTable",
                "CtxTruncateTable", "CtxDropTable", "CtxRefreshNode", "CtxRefreshConnection", "CtxDisconnect",
                "CtxConnectionMonitor", "CtxMoveToGroup", "CtxRenameGroup", "CtxDeleteGroup",
            ];
            Assert.Empty(expected.Except(names!));
            Assert.Contains("danger", window.FindControl<MenuItem>("CtxDropTable")!.Classes);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ContextMenuSeparators_ShowOnlyBetweenVisibleGroups()
    {
        Control Item(bool visible) => new MenuItem { Header = "x", IsVisible = visible };
        var leading = new Separator();
        var first = new Separator();
        var second = new Separator();
        var trailing = new Separator();
        var menu = new ContextMenu { Items = { leading, Item(true), first, Item(false), second, Item(true), trailing, Item(false) } };

        SchemaTreeContextMenu.CollapseSeparators(menu);

        Assert.False(leading.IsVisible);
        Assert.False(first.IsVisible);
        Assert.True(second.IsVisible);
        Assert.False(trailing.IsVisible);
    }

    [AvaloniaFact]
    public async Task ObjectsTab_ListsTheSeededTablesWithRows_AndDashesForWhatSQLiteLacks()
    {
        await using var shell = await Shell.ConnectAsync(() => CreateMainWindowViewModel());
        var objects = shell.ViewModel.Frame.Objects;
        await objects.ReloadAsync();
        shell.Pump();

        Assert.True(shell.ViewModel.Frame.Strip.IsObjectsSelected);
        Assert.Same(objects, shell.ViewModel.Frame.Strip.Items[0]);
        Assert.Contains(objects.Rows, row => row.Name == shell.Table.RawName || row.Name == shell.Table.Name);
        Assert.All(objects.Rows.Where(r => r.Node?.Type == SchemaNodeType.Table), row =>
        {
            Assert.NotEqual(ObjectRow.Unknown, row.Rows);
            Assert.Equal(ObjectRow.Unknown, row.Engine);
        });
        Assert.StartsWith($"{objects.Rows.Count} tables", objects.FooterText);

        var grid = shell.Window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "ObjectsGrid");
        Assert.True(grid.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task SelectingAnObjectsRow_FillsTheInformationPane_AndTheObjectsTabCannotClose()
    {
        await using var shell = await Shell.ConnectAsync(() => CreateMainWindowViewModel());
        var objects = shell.ViewModel.Frame.Objects;
        await objects.ReloadAsync();
        var pane = shell.ViewModel.Frame.Information;
        Assert.False(pane.HasSubject);

        objects.SelectedRow = objects.Rows.First(r => r.Node?.Type == SchemaNodeType.Table);
        shell.Pump();
        Assert.True(pane.HasSubject);
        Assert.Equal(objects.SelectedRow.Name, pane.Name);
        Assert.Contains(pane.General, item => item.Label == "Rows" && item.Value == objects.SelectedRow.Rows);
        Assert.Contains(pane.General, item => item.Label == "Engine" && item.Value == ObjectRow.Unknown);
        await WaitUntilAsync(() => pane.Ddl.Contains("CREATE", StringComparison.OrdinalIgnoreCase));

        Assert.False(objects.ShowsCloseButton);
        await shell.ViewModel.CloseTabCommand.ExecuteAsync(objects);
        Assert.Same(objects, shell.ViewModel.Frame.Strip.Items[0]);
    }

    [AvaloniaFact]
    public async Task TogglingTheInformationPane_LeavesTheNavigatorWidthAlone()
    {
        await using var shell = await Shell.ConnectAsync(() => CreateMainWindowViewModel());
        var tree = shell.Window.FindControl<TreeView>("SchemaTree")!;
        var pane = shell.Window.FindControl<InformationPaneView>("InformationPane")!;
        var before = tree.Bounds.Width;
        Assert.True(pane.IsEffectivelyVisible);
        Assert.Equal(260, pane.Bounds.Width, 1.0);

        shell.ViewModel.Sidebar.InformationPaneVisible = false;
        shell.Pump();
        Assert.False(pane.IsEffectivelyVisible);
        Assert.Equal(before, tree.Bounds.Width, 0.5);

        shell.ViewModel.Sidebar.InformationPaneVisible = true;
        shell.Pump();
        Assert.True(pane.IsEffectivelyVisible);
        Assert.Equal(before, tree.Bounds.Width, 0.5);
    }

    [AvaloniaFact]
    public void FavoritesMenu_OnlyTheFavouritesOpenAFavourite()
    {
        var window = new MainWindow { DataContext = CreateMainWindowViewModel() };
        try
        {
            window.Show();
            var vm = (MainWindowViewModel)window.DataContext!;
            var list = window.FindControl<MenuItem>("FavoritesList")!;

            // The submenu header is not a favourite: it must not carry OpenFavorite with the window's view model.
            Assert.Null(list.Command);
            Assert.Null(list.CommandParameter);

            var menu = window.FindControl<MenuItem>("FavoritesMenu")!;
            menu.IsSubMenuOpen = true; // reloads the (empty) saved favourites
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            vm.Frame.Menu.Favorites.Add(new SavedQueryDefinition { Name = "orders", Sql = "SELECT 1" });
            list.IsSubMenuOpen = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var item = Assert.IsType<MenuItem>(list.ContainerFromIndex(0));
            Assert.Same(vm.Frame.Menu.OpenFavoriteCommand, item.Command);
            Assert.IsType<SavedQueryDefinition>(item.CommandParameter);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        Assert.True(condition());
    }

    /// <summary>The main window connected to a copy of the seeded database, with the first table found.</summary>
    private sealed class Shell : IAsyncDisposable
    {
        private readonly string _dbPath;

        private Shell(MainWindowViewModel viewModel, MainWindow window, SchemaNode table, string dbPath)
        {
            ViewModel = viewModel;
            Window = window;
            Table = table;
            _dbPath = dbPath;
        }

        public MainWindowViewModel ViewModel { get; }
        public MainWindow Window { get; }
        public SchemaNode Table { get; }

        public static async Task<Shell> ConnectAsync(Func<MainWindowViewModel> create)
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"rain-shell-{Guid.NewGuid():N}.db");
            File.Copy(UiSnapshotDialogs.FindSeededDatabase(), dbPath);
            var vm = create();
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            await vm.AddConnectionAsync(new ConnectionInfo { Name = "Shop", Provider = "SQLite", ConnectionString = $"Data Source={dbPath}" });
            var table = await UiSnapshotDialogs.ExpandToFirstTableAsync(vm, vm.SchemaNodes[0]);
            Assert.NotNull(table);
            vm.SelectedSchemaNode = null;
            HeadlessFrames.Pump(window);
            return new Shell(vm, window, table, dbPath);
        }

        public void Pump() => HeadlessFrames.Pump(Window);

        public ValueTask DisposeAsync()
        {
            Window.Close();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(_dbPath); }
            catch (IOException) { } // a tool tab may still hold the file; the temp copy is harmless
            return ValueTask.CompletedTask;
        }
    }
}
