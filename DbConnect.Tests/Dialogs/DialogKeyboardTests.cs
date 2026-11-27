using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.Tests.Architecture;
using DbConnect.Tests.UiSnapshots;
using DbConnect.ViewModels;

namespace DbConnect.Tests.Dialogs;

/// <summary>
/// Ticket 07 (D1): in every dialog Enter runs the default button's action and Esc closes the window. Each dialog is
/// built the way production opens it (see <see cref="UiSnapshotDialogs"/>), against a copy of the seeded database.
/// </summary>
public sealed class DialogKeyboardTests
{
    /// <summary>Default actions that reach outside the dialog (write files, run SQL): Enter must press them, but they are held back.</summary>
    private static readonly HashSet<string> HeldBackActions = new(StringComparer.Ordinal)
    {
        "ExportDataDialog",      // writes one file per table
        "ProcedureRunnerDialog", // runs the routine
    };

    /// <summary>Default actions that keep the dialog open: a wizard's Next moves to its next step.</summary>
    private static readonly HashSet<string> StaysOpen = new(StringComparer.Ordinal) { "CsvImportWizardView" };

    // One connected main window model for the whole class: connecting per case started a schema indexer and a
    // table-data load each time, background work that outlived its test. Headless tests share one UI thread.
    private static Task<UiSnapshotContext>? s_context;

    public static TheoryData<string> Dialogs => new(UiSnapshotDialogs.DialogTypes().Select(t => t.Name));

    [AvaloniaTheory]
    [MemberData(nameof(Dialogs))]
    public async Task Enter_RunsTheDefaultAction(string name)
    {
        if (HasNoDialogKeys(name)) return;
        var dialog = await ShowAsync(name);
        try
        {
            var pressed = PressEnter(dialog, holdBack: HeldBackActions.Contains(name));

            // A dialog may swap buttons between steps (Next, then Close), but only one default shows at a time.
            var button = Assert.Single(Buttons(dialog), b => b.IsDefault);
            Assert.True(button.IsEffectivelyEnabled, $"{name}: the default button is disabled");
            Assert.Same(button, pressed);
            if (!HeldBackActions.Contains(name))
                Assert.True(dialog.IsVisible == StaysOpen.Contains(name),
                    StaysOpen.Contains(name) ? $"{name} closed on Enter" : $"{name}: Enter pressed the default button but the dialog stayed open");
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public void ReadOnlyBlobViewer_ClosesOnEnter()
    {
        // Read-only, OK is hidden and Close is the default as well as the cancel button.
        var dialog = new DbConnect.Views.BlobEditorDialog([1, 2, 3], "thumbnail", allowEdit: false);
        dialog.Show();
        HeadlessFrames.Pump(dialog);
        try
        {
            var close = Assert.Single(Buttons(dialog), b => b.IsDefault);
            Assert.Same(close, PressEnter(dialog, holdBack: false));
            Assert.False(dialog.IsVisible);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Dialogs))]
    public async Task Escape_ClosesTheDialog(string name)
    {
        var dialog = await ShowAsync(name);
        try
        {
            if (!HasNoDialogKeys(name))
                Assert.Single(Buttons(dialog), b => b.IsCancel);

            dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            HeadlessFrames.Pump(dialog);

            Assert.False(dialog.IsVisible, $"{name} is still open after Esc");
        }
        finally { dialog.Close(); }
    }

    // Windows whose list acts on Enter (command palette, global search) have no default button; Esc still closes them.
    private static bool HasNoDialogKeys(string name) =>
        ViewStylingBudgets.WindowsWithoutDialogKeys.ContainsKey(name + ".axaml");

    /// <summary>Presses Enter and returns the button it clicked. Held back, the click is marked handled before the
    /// dialog's own handlers and command run (a class handler runs first), so the action does not happen.</summary>
    private static Button? PressEnter(Window dialog, bool holdBack)
    {
        Button? pressed = null;
        using var probe = Button.ClickEvent.AddClassHandler<Button>((sender, e) =>
        {
            if (sender.GetVisualRoot() != dialog) return;
            pressed ??= sender;
            if (holdBack) e.Handled = true;
        });
        dialog.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        HeadlessFrames.Pump(dialog);
        return pressed;
    }

    private static async Task<Window> ShowAsync(string name)
    {
        var type = UiSnapshotDialogs.DialogTypes().Single(t => t.Name == name);
        var fixtures = UiSnapshotDialogs.Build(await (s_context ??= ConnectAsync()));
        var dialog = await UiSnapshotDialogs.CreateAsync(type, fixtures, message => Assert.Fail($"{name}: {message}"));
        dialog!.Show();
        HeadlessFrames.Pump(dialog);
        if (dialog.DataContext is ForeignKeyLookupViewModel lookup)
            await PickFirstValueAsync(dialog, lookup);
        return dialog;
    }

    // Connects to a copy of the seeded database and opens a table-data tab (the foreign-key lookup needs one).
    private static async Task<UiSnapshotContext> ConnectAsync()
    {
        var database = Path.Combine(Path.GetTempPath(), $"rain-dialog-keys-{Guid.NewGuid():N}.db");
        File.Copy(UiSnapshotDialogs.FindSeededDatabase(), database);
        var main = new MainWindowViewModel(new DatabaseService());
        await main.AddConnectionAsync(new ConnectionInfo { Name = "Shop (SQLite)", Provider = "SQLite", ConnectionString = $"Data Source={database}" });
        var table = await UiSnapshotDialogs.ExpandToFirstTableAsync(main, main.SchemaNodes[0])
            ?? throw new InvalidOperationException("The seeded database has no table.");
        main.OpenTableDataTab(table);
        await Task.Delay(300);
        return new UiSnapshotContext(main, main.SchemaTree.FindSessionForNode(table)!, table);
    }

    // The lookup loads its values when it opens; its default button needs a chosen value, as it would for a user.
    private static async Task PickFirstValueAsync(Window dialog, ForeignKeyLookupViewModel lookup)
    {
        for (var i = 0; i < 100 && (lookup.IsLoading || lookup.Values.Count == 0); i++)
        {
            await Task.Delay(20);
            HeadlessFrames.Pump(dialog);
        }
        lookup.SelectedValue = lookup.Values.FirstOrDefault();
        HeadlessFrames.Pump(dialog);
    }

    private static IEnumerable<Button> Buttons(Window dialog) =>
        dialog.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible);
}
