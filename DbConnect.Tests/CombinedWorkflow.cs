using System.Globalization;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>
/// The public steps issue 16 runs on every provider: script a table and recreate it elsewhere, open it
/// through explorer → Design Table…, make several kinds of edit in one save, reopen, and check what
/// changed, what did not, and that explorer and completion agree.
/// </summary>
internal static class CombinedWorkflow
{
    public static DatabaseSession Session(IDatabaseProvider provider, string connection) =>
        new(new ConnectionInfo { Name = "combined", Provider = provider.ProviderName, ConnectionString = connection }, provider);

    /// <summary>Script as CREATE through the public scripting entry point; the script must be complete.</summary>
    public static async Task<string> ScriptAsync(DatabaseSession session, string? schema, string table)
    {
        var result = await TableScripting.ScriptCreateAsync(session, new DatabaseObjectIdentity(null, schema, table, SchemaNodeType.Table));
        Assert.Null(result.Error);
        return result.Sql!;
    }

    /// <summary>Connects the session to the window and opens the table with explorer → Design Table….</summary>
    public static async Task<TableDesignerViewModel> OpenAsync(MainWindowViewModel vm, DatabaseSession session, string? schema, string table)
    {
        var root = await TableDesignRoundTrip.ConnectAsync(vm, session);
        return await OpenAgainAsync(vm, root, schema, table);
    }

    public static ColumnDefinition Column(this TableDesignerViewModel designer, string name) => designer.Columns.Single(c => c.Name == name);

    /// <summary>Adds an ordered unique key with the Constraints tab's Unique button and column picker.</summary>
    public static void AddUniqueKey(this TableDesignerViewModel designer, string name, params string[] columns)
    {
        designer.Keys.AddUniqueKeyCommand.Execute(null);
        foreach (var column in columns)
        {
            designer.Keys.NewMember = column;
            designer.Keys.AddMemberCommand.Execute(null);
        }
        designer.Keys.Selected!.Name = name;
    }

    public static async Task<TableDesignerViewModel> OpenAgainAsync(MainWindowViewModel vm, SchemaNode root, string? schema, string table)
    {
        var node = await TableDesignRoundTrip.FindTableAsync(vm, root, schema, table);
        var designer = await TableDesignRoundTrip.OpenAsync(vm, node);
        Assert.True(designer.Design.IsLoaded, designer.Design.Error ?? designer.StatusMessage);
        Assert.True(designer.Design.SaveBlockedReason == null, designer.Design.SaveBlockedReason);
        Assert.Equal("", designer.PreviewSql);
        return designer;
    }

    /// <summary>
    /// Saves the reviewed plan. Save is pressed twice before the first finishes: the second press must not
    /// start another save. Returns the SQL that was reviewed, which must be the SQL that ran.
    /// </summary>
    public static async Task<string> SaveAsync(TableDesignerViewModel designer)
    {
        // Referenced tables' columns load in the background; foreign-key checks wait for them.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (designer.ValidationErrors.Any(e => e.Contains("still loading", StringComparison.Ordinal)) && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(designer.Plan.CanExecute, string.Join("\n", designer.ValidationErrors.Concat(designer.Plan.Blocked)) + "\n" + designer.PreviewSql);
        var reviewed = designer.PreviewSql;
        designer.Review.DestructiveConfirmed = designer.Review.NeedsConfirmation;
        Assert.True(designer.PlanAllowsSave, designer.Design.SaveBlockedReason);
        var planned = designer.Plan.Steps.ToArray();
        Assert.Equal(Squeezed(reviewed), Squeezed(string.Concat(planned)));
        var first = designer.SaveCommand.ExecuteAsync(null);
        Assert.True(designer.IsSaving);
        var second = designer.SaveCommand.ExecuteAsync(null);
        Assert.True(second.IsCompleted, "A second Save started while the first was running.");
        await Task.WhenAll(first, second);
        Assert.True(designer.StatusMessage?.Contains("successfully") == true, designer.StatusMessage + "\n" + reviewed);
        // The steps that ran are the reviewed ones, each applied once.
        Assert.Equal(planned, designer.Review.Steps.Select(s => s.Sql));
        Assert.All(designer.Review.Steps, s => Assert.Equal("Applied", s.Status));
        Assert.Equal("", designer.PreviewSql);
        return reviewed;
    }

    private static string Squeezed(string sql) => new(sql.Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>The engine refuses the statement, and its error names the rule that refused it.</summary>
    public static async Task RefusedAsync(IDatabaseProvider provider, string connection, string sql, string rule)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => provider.ExecuteNonQueryAsync(connection, sql));
        Assert.True(error.Message.Contains(rule, StringComparison.OrdinalIgnoreCase), $"Expected a refusal by {rule}, got: {error.Message}");
    }

    /// <summary>Saving again with nothing edited runs nothing.</summary>
    public static async Task AssertNoOpAsync(TableDesignerViewModel designer)
    {
        await designer.SaveCommand.ExecuteAsync(null);
        Assert.Equal("No changes to save.", designer.StatusMessage);
    }

    /// <summary>
    /// The design lines that changed between two loads: those only before, and those only after. Every
    /// other line (columns, keys, checks, indexes, triggers, options, comments) must be identical.
    /// </summary>
    public static (string[] Removed, string[] Added) Diff(TableDesign before, TableDesign after)
    {
        var b = TableDesignText.Lines(before);
        var a = TableDesignText.Lines(after);
        return (b.Except(a).ToArray(), a.Except(b).ToArray());
    }

    public static void AssertChanged(TableDesign before, TableDesign after, string[] removed, string[] added)
    {
        var (r, a) = Diff(before, after);
        Assert.True(removed.Order(StringComparer.Ordinal).SequenceEqual(r.Order(StringComparer.Ordinal)) &&
                    added.Order(StringComparer.Ordinal).SequenceEqual(a.Order(StringComparer.Ordinal)),
            "Removed:\n" + string.Join("\n", r) + "\nAdded:\n" + string.Join("\n", a));
    }

    public static async Task<string[]> RowsAsync(IDatabaseProvider provider, string connection, string sql) =>
        (await provider.ExecuteQueryAsync(connection, sql)).ObjectRows
            .Select(r => string.Join("|", r.Select(v => v is null or DBNull ? "<null>" : Convert.ToString(v, CultureInfo.InvariantCulture)))).ToArray();

    /// <summary>Completion after "SELECT table." lists the table's current columns.</summary>
    public static async Task<string[]> CompleteColumnsAsync(DatabaseSession session, string qualifiedTable)
    {
        var sql = "SELECT " + qualifiedTable + ".";
        return (await CatalogSqlCompletion.GetCandidatesAsync(sql, sql.Length, session)).Select(c => c.Text).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>The explorer's column nodes for the table after its folder is reloaded.</summary>
    public static async Task<string[]> ExplorerColumnsAsync(MainWindowViewModel vm, SchemaNode root, string? schema, string table)
    {
        var node = await TableDesignRoundTrip.FindTableAsync(vm, root, schema, table);
        await vm.SchemaTree.LoadSchemaNodeChildrenAsync(node);
        return node.Children.Where(c => c.Type == SchemaNodeType.Column).Select(c => c.RawName ?? c.Name).ToArray();
    }

    /// <summary>
    /// A second designer opened before the save: its edit is now stale, so Save is refused with the change
    /// named, nothing runs, and its edits stay until Reload.
    /// </summary>
    public static async Task AssertStaleAsync(TableDesignerViewModel stale, Action<TableDesignerViewModel> edit, string changed)
    {
        edit(stale);
        stale.Review.DestructiveConfirmed = stale.Review.NeedsConfirmation;
        await stale.SaveCommand.ExecuteAsync(null);
        Assert.Contains("The table changed since it was loaded", stale.StatusMessage);
        Assert.True(stale.StatusMessage!.Contains(changed, StringComparison.Ordinal), stale.StatusMessage);
        Assert.True(stale.Review.RequiresReload);
        Assert.False(stale.PlanAllowsSave);
    }
}
