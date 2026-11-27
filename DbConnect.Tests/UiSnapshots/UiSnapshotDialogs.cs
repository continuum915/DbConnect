using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests.UiSnapshots;

/// <summary>What a dialog fixture can draw on: the connected window's view model, its session and a table.</summary>
public sealed record UiSnapshotContext(MainWindowViewModel Main, DatabaseSession Session, SchemaNode Table);

/// <summary>
/// Builds each dialog the way production opens it, with representative content, so screenshots show
/// populated forms rather than empty DataContexts. Dialogs not listed here fall back to their
/// parameterless constructor in <see cref="UiSnapshotCapture"/>.
/// </summary>
public static class UiSnapshotDialogs
{
    private const string SampleSql = "UPDATE customers SET email = @email WHERE customer_id = @id;";

    public static IReadOnlyDictionary<Type, Func<Task<Window>>> Build(UiSnapshotContext context)
    {
        var (main, session, table) = context;
        return new Dictionary<Type, Func<Task<Window>>>
        {
            [typeof(AddTableDialog)] = () => Ready(new AddTableDialog { DataContext = new AddTableDialogViewModel(session) }),
            [typeof(BlobEditorDialog)] = () => Ready(new BlobEditorDialog(SampleBytes(), "thumbnail", allowEdit: true)),
            [typeof(CellValueViewerWindow)] = () => Ready(new CellValueViewerWindow(new CellValueViewerViewModel(
                """{"id": 42, "name": "Ada", "tags": ["vip", "newsletter"], "address": {"city": "London"}}""", "payload"))),
            [typeof(ChangePasswordDialog)] = () => Ready(Typed(new ChangePasswordDialog("report_reader",
                (user, _) => $"ALTER USER {user} WITH PASSWORD '********';"), ("PasswordBox", "n3w-secret"))),
            [typeof(CommandPaletteView)] = () => Ready(new CommandPaletteView { DataContext = new CommandPaletteViewModel(PaletteActions()) }),
            [typeof(CommitPreviewDialog)] = () => Ready(new CommitPreviewDialog(new CommitPreviewRequest(
                "UPDATE \"customers\" SET \"email\" = @p0 WHERE \"customer_id\" = @p1;\nDELETE FROM \"customers\" WHERE \"customer_id\" = @p2;",
                "2 changes will be committed in one transaction."))),
            [typeof(ConcurrencyConflictDialog)] = () => Ready(new ConcurrencyConflictDialog(new ConcurrencyConflictRequest(
                new EditableRow(new Dictionary<string, object?> { ["customer_id"] = 7, ["email"] = "ada@example.com" }),
                "Someone else changed this row since the query ran. Reload it to see their version and drop your edit, or overwrite their change with yours.",
                CanOverwrite: true))),
            [typeof(ConfirmDialog)] = () => Ready(new ConfirmDialog("Drop table",
                $"Drop table {table.Name}? This cannot be undone.", "Drop", "Cancel")),
            [typeof(CreateUserDialog)] = () => Ready(Typed(new CreateUserDialog((user, _) => $"CREATE USER {user} WITH PASSWORD '********';"),
                ("UsernameBox", "report_writer"), ("PasswordBox", "s3cret"))),
            [typeof(CsvImportWizardView)] = () => Ready(new CsvImportWizardView { DataContext = new CsvImportWizardViewModel(session) }),
            [typeof(DumpSqlFileDialog)] = () => Ready(new DumpSqlFileDialog
            {
                DataContext = new ExportOperationViewModel
                {
                    Message = $"Completed · Dump SQL File (Structure Only) · 12 tables · 0.4s · {Path.Combine(Path.GetTempPath(), "shop.sql")}",
                },
            }),
            [typeof(DumpTablesDialog)] = () => Ready(new DumpTablesDialog
            {
                DataContext = new DumpTablesViewModel(ExportChoices(main, session, table), DumpMode.StructureAndData),
            }),
            [typeof(ExportDataDialog)] = () => Ready(new ExportDataDialog
            {
                DataContext = new ExportDataViewModel(session, ExportChoices(main, session, table)) { Folder = Path.GetTempPath() },
            }),
            [typeof(ForeignKeyLookupDialog)] = () => ForeignKeyLookupAsync(main, table),
            [typeof(JobEditorDialog)] = () => Ready(new JobEditorDialog
            {
                DataContext = new JobEditorDialogViewModel
                {
                    Name = "Nightly order report",
                    ConnectionName = "Shop (SQLite)",
                    SqlOrFilePath = "SELECT * FROM orders WHERE order_date > date('now', '-1 day');",
                },
            }),
            [typeof(GlobalSearchOverlay)] = () => Ready(new GlobalSearchOverlay { DataContext = new GlobalSearchViewModel(main) }),
            [typeof(GoToLineDialog)] = () => Ready(Typed(new GoToLineDialog(240), ("LineNumberBox", "120"))),
            [typeof(GrantPrivilegeDialog)] = () => Ready(new GrantPrivilegeDialog("report_reader",
                ["SELECT", "INSERT", "UPDATE", "DELETE"], (user, privilege) => $"GRANT {privilege ?? "SELECT"} ON customers TO {user};")),
            [typeof(GroupInputDialog)] = () => Ready(new GroupInputDialog("Group name:", "Production")),
            [typeof(GroupPickerDialog)] = () => Ready(new GroupPickerDialog(["Production", "Staging", "Local"])),
            [typeof(PreferencesDialog)] = () => Ready(new PreferencesDialog
            {
                DataContext = new PreferencesViewModel(new AppSettings(), "Light", null, _ => { }, _ => Task.CompletedTask),
            }),
            [typeof(ProcedureRunnerDialog)] = () => ProcedureRunnerAsync(session),
            [typeof(ProductionConfirmDialog)] = () => Ready(new ProductionConfirmDialog
            {
                DataContext = new ProductionConfirmViewModel(new DestructiveActionPreview(
                    "Drop", "Table", table.Name, "Shop (SQLite)", "Production", $"DROP TABLE \"{table.Name}\";")),
            }),
            [typeof(QueryParamDialog)] = () => Ready(new QueryParamDialog
            {
                DataContext = new QueryParamDialogViewModel(SampleSql, SqlParameterDetector.Detect(SampleSql, CancellationToken.None),
                    new QueryParamContextService(), session.ProviderName, session.Connection.Name, session.DatabaseLabel),
            }),
            [typeof(SaveChangesDialog)] = () => Ready(new SaveChangesDialog(["Query 1", "Query 3", $"{table.Name} (data)"])),
            [typeof(SnippetEditorDialog)] = () => Ready(new SnippetEditorDialog
            {
                DataContext = new SnippetEditorDialogViewModel(new Snippet
                {
                    Name = "Recent orders",
                    Description = "Orders placed in the last week",
                    Body = "SELECT * FROM orders WHERE order_date > date('now', '-7 days');",
                    Tags = ["orders", "report"],
                }),
            }),
            [typeof(SshTrustDialog)] = () => Ready(SshTrust()),
            [typeof(SupportBundleDialog)] = () => Ready(new SupportBundleDialog
            {
                DataContext = new SupportBundleViewModel(new SupportBundleDraft(DateTimeOffset.Now,
                [
                    new SupportBundleEntry("environment.txt", "text/plain", "DbConnect 1.0\nOS: Windows 11\n.NET 10"),
                    new SupportBundleEntry("connections.json", "application/json", """[{"name": "Shop", "provider": "SQLite"}]"""),
                ]))
                {
                    SaveRequested = () => Task.FromResult(true), // as if the user picked a destination
                },
            }),
        };
    }

    /// <summary>Every window in DbConnect.Views except the main window, by name.</summary>
    public static IEnumerable<Type> DialogTypes() =>
        typeof(MainWindow).Assembly.GetTypes()
            .Where(t => typeof(Window).IsAssignableFrom(t) && !t.IsAbstract && t != typeof(MainWindow) && t.Namespace == "DbConnect.Views")
            .OrderBy(t => t.Name, StringComparer.Ordinal);

    /// <summary>
    /// Builds a dialog from its fixture, or from its parameterless constructor with a view model found by name.
    /// Returns null (and reports why) when neither works.
    /// </summary>
    public static async Task<Window?> CreateAsync(Type type, IReadOnlyDictionary<Type, Func<Task<Window>>> fixtures, Action<string> report)
    {
        Window dialog;
        if (fixtures.TryGetValue(type, out var build)) dialog = await build();
        else if (type.GetConstructor(Type.EmptyTypes) is { } ctor) dialog = (Window)ctor.Invoke(null);
        else { report("no parameterless constructor"); return null; }

        var vmType = FindViewModelType(type.Name);
        if (vmType?.GetConstructor(Type.EmptyTypes) is { } vmCtor && dialog.DataContext == null)
        {
            try { dialog.DataContext = vmCtor.Invoke(null); }
            catch (Exception e) { report($"VM {vmType.Name} failed: {e.GetBaseException().Message}"); }
        }
        return dialog;
    }

    private static Type? FindViewModelType(string viewName)
    {
        var assembly = typeof(MainWindow).Assembly;
        var stem = viewName.EndsWith("Dialog") ? viewName[..^6] : viewName.EndsWith("Window") ? viewName[..^6] : viewName;
        return assembly.GetType($"DbConnect.ViewModels.{viewName}ViewModel") ?? assembly.GetType($"DbConnect.ViewModels.{stem}ViewModel")
            ?? assembly.GetType($"DbConnect.ViewModels.{stem}DialogViewModel");
    }

    /// <summary>The scaffolder's seeded e-commerce SQLite database (copy it before use).</summary>
    public static string FindSeededDatabase()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DbConnect.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "DbConnect.DatabaseScaffolder", "rain_ecommerce_template.db");
    }

    /// <summary>Expands the schema tree down to its first table node.</summary>
    public static async Task<SchemaNode?> ExpandToFirstTableAsync(MainWindowViewModel vm, SchemaNode node, int depth = 0)
    {
        if (node.Type == SchemaNodeType.Table) return node;
        if (depth > 5) return null;
        if (node.IsLazyLoadable && !node.IsLoaded) await vm.LoadSchemaNodeChildrenAsync(node);
        node.IsExpanded = true;
        foreach (var child in node.Children.ToList())
        {
            if (child.Type == SchemaNodeType.Table) return child;
            if (child.Type is SchemaNodeType.Column or SchemaNodeType.Index) continue;
            var found = await ExpandToFirstTableAsync(vm, child, depth + 1);
            if (found != null) return found;
        }
        return null;
    }

    private static Task<Window> Ready(Window window) => Task.FromResult(window);

    // What a user would have typed before pressing the default button.
    private static Window Typed(Window window, params (string Box, string Text)[] entries)
    {
        foreach (var (box, text) in entries)
            window.FindControl<TextBox>(box)!.Text = text;
        return window;
    }

    private static byte[] SampleBytes() => [.. Enumerable.Range(0, 96).Select(i => (byte)(i * 7))];

    private static IEnumerable<CommandPaletteViewModel.PaletteAction> PaletteActions()
    {
        var noop = new RelayCommand(() => { });
        return
        [
            new("New Query", "File", noop), new("Open Connection", "File", noop), new("Format SQL", "Edit", noop),
            new("Execute Query", "Query", noop), new("Explain Plan", "Query", noop), new("ER Diagram", "Tools", noop),
        ];
    }

    private static List<ExportTableChoice> ExportChoices(MainWindowViewModel main, DatabaseSession session, SchemaNode table)
    {
        var siblings = main.SchemaTree.SchemaNodes.SelectMany(Descend)
            .FirstOrDefault(n => n.Children.Contains(table))?.Children ?? [table];
        return [.. siblings.Where(n => n.Type is SchemaNodeType.Table or SchemaNodeType.View).Take(8)
            .Select(n => new ExportTableChoice(n.RawName ?? n.Name, main.SchemaTree.GetIdentity(n, session), ReferenceEquals(n, table)))];
    }

    private static IEnumerable<SchemaNode> Descend(SchemaNode node) => [node, .. node.Children.SelectMany(Descend)];

    private static async Task<Window> ForeignKeyLookupAsync(MainWindowViewModel main, SchemaNode table)
    {
        var tab = main.Tabs.OfType<TableDataTabViewModel>().FirstOrDefault()
            ?? throw new InvalidOperationException("No table data tab is open.");
        await tab.LoadForeignKeysAsync();
        var relationship = tab.ForeignKeys.FirstOrDefault()
            ?? new ForeignKeyRelationship("fk_sample", table.Name, table.Name, ["id"], ["id"]);
        var lookup = new ForeignKeyLookupViewModel(tab, relationship);
        await lookup.LoadAsync();
        return new ForeignKeyLookupDialog(lookup);
    }

    private static async Task<Window> ProcedureRunnerAsync(DatabaseSession session)
    {
        var vm = new ProcedureRunnerViewModel(session, "dbo", "update_stock", SchemaNodeType.Procedure, "dbo.update_stock",
        [
            new() { Name = "product_id", DataType = "int", Direction = ParameterDirection.In, DefaultValue = "1", OrdinalPosition = 1 },
            new() { Name = "quantity", DataType = "int", Direction = ParameterDirection.In, DefaultValue = "5", OrdinalPosition = 2 },
            new() { Name = "new_stock", DataType = "int", Direction = ParameterDirection.Out, OrdinalPosition = 3 },
        ]);
        try { await vm.InitializeAsync(); }
        catch (NotSupportedException) { } // SQLite has no routines; the detected parameters still populate the form.
        return new ProcedureRunnerDialog { DataContext = vm };
    }

    // As SshTrustDialog.PromptAsync opens it: the view model's decision closes the dialog.
    private static SshTrustDialog SshTrust()
    {
        var vm = new SshTrustPromptViewModel(new SshTrustRequest(
            new SshHostKey("db.example.com", 22, "ssh-ed25519", "SHA256:nThbg6kXUpJWGl7E1IGOCspRomTxdCARLviKw6E5SY8"), null));
        var dialog = new SshTrustDialog { DataContext = vm };
        vm.Complete = decision => dialog.Close(decision);
        return dialog;
    }
}
