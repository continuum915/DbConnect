using DbConnect.Models;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>
/// The unrelated-edit round trip of issue 06 through the public workflow: open the table with
/// explorer → Design Table…, check the designer shows the loaded design, add a nullable column,
/// save, reopen and compare designs. Where the save path would lose something, Save must be
/// blocked with its reason instead.
/// </summary>
internal static class TableDesignRoundTrip
{
    public const string AddedColumn = "rt_added";

    /// <summary>Registers the session's explorer root with the window, as connecting does.</summary>
    public static async Task<SchemaNode> ConnectAsync(MainWindowViewModel vm, DatabaseSession session)
    {
        var root = Assert.Single(await session.GetSchemaAsync());
        vm.ActiveConnections[root] = session.Connection;
        vm.ActiveSessions[root] = session;
        vm.ActiveSessionOptions.Add(session);
        vm.SchemaNodes.Add(root);
        return root;
    }

    /// <summary>Finds the table's explorer node, expanding only its schema's folders.</summary>
    public static async Task<SchemaNode> FindTableAsync(MainWindowViewModel vm, SchemaNode root, string? schema, string name)
    {
        var pending = new Queue<SchemaNode>([root]);
        while (pending.Count > 0)
        {
            var node = pending.Dequeue();
            foreach (var child in node.Children.ToList())
            {
                if (child.Type == SchemaNodeType.Table && child.RawName == name
                    && (schema == null || node.Type != SchemaNodeType.TableFolder || FolderSchema(child) is not { } s || s == schema))
                    return child;
                var expand = child.Type switch
                {
                    SchemaNodeType.Schema => schema == null || child.RawName == schema,
                    SchemaNodeType.Database => true,
                    SchemaNodeType.TableFolder => true,
                    _ => false
                };
                if (!expand) continue;
                await vm.SchemaTree.LoadSchemaNodeChildrenAsync(child);
                pending.Enqueue(child);
            }
        }
        throw new InvalidOperationException($"Table {schema}.{name} is not in the explorer.");
    }

    private static string? FolderSchema(SchemaNode table) => table.Identity?.Schema;

    /// <summary>Opens the designer the way explorer → Design Table… does and waits for both loads.</summary>
    public static async Task<TableDesignerViewModel> OpenAsync(MainWindowViewModel vm, SchemaNode table)
    {
        vm.Tools.OpenTableDesignerTab(table);
        var designer = Assert.IsType<TableDesignerViewModel>(vm.SelectedTab);
        await designer.LoadAsync();
        return designer;
    }

    /// <summary>The designer shows every loaded element on its tabs.</summary>
    public static void AssertShown(TableDesignerViewModel designer, TableDesign design)
    {
        Assert.Equal(TableDesignLoadState.Loaded, designer.Design.State);
        Assert.Equal(TableDesignText.Lines(design), TableDesignText.Lines(designer.Design.Baseline!));
        foreach (var column in designer.Design.Baseline!.Columns)
            Assert.Same(column, Assert.Single(designer.Columns, c => c.Name == column.Name).Loaded);
        foreach (var index in designer.Design.Baseline.Indexes)
            Assert.Same(index, Assert.Single(designer.Indexes.Items, i => i.Name == index.Name).Loaded);
        Assert.Equal(design.ForeignKeys.Count, designer.ForeignKeys.Items.Count);
        // Keys load into the editable key list (issue 11); checks into the read-only constraint rows.
        Assert.Equal((design.PrimaryKey == null ? 0 : 1) + design.UniqueKeys.Count, designer.Keys.Items.Count);
        Assert.Equal(design.Checks.Count, designer.Checks.Items.Count);
        Assert.Equal(design.Triggers, designer.Design.Triggers);
        Assert.Equal(design.Options, designer.Design.Options);
        Assert.Equal(design.Unrepresented.Select(g => g.Description), designer.Design.Unrepresented);
    }

    /// <summary>
    /// Adds a nullable column and saves, then compares the reopened design with the original. Returns
    /// the elements the save lost, or null when Save was blocked (the reason is asserted to be shown).
    /// </summary>
    public static async Task<List<string>?> RunAsync(MainWindowViewModel vm, SchemaNode table, DatabaseSession session,
        DatabaseObjectIdentity identity, string addedType, Func<TableDesign, IReadOnlyList<string>>? lines = null)
    {
        lines ??= TableDesignText.Lines;
        var source = (ITableDesignSource)session.Provider;
        var designer = await OpenAsync(vm, table);
        Assert.Equal(identity.Name, designer.Design.Table!.Name);
        if (identity.Schema != null) Assert.Equal(identity.Schema, designer.Design.Table.Schema);
        identity = designer.Design.Table;
        var before = await source.GetTableDesignAsync(session.ConnectionString, identity);
        AssertShown(designer, before);
        designer.Columns.Add(new ColumnDefinition { Name = AddedColumn, DataType = addedType, IsNullable = true, IsNew = true });
        designer.UpdatePreview();
        var preview = designer.PreviewSql;
        await designer.SaveCommand.ExecuteAsync(null);
        if (designer.Design.SaveBlockedReason is { } reason)
        {
            Assert.Contains(reason, designer.StatusMessage);
            Assert.DoesNotContain((await source.GetTableDesignAsync(session.ConnectionString, identity)).Columns, c => c.Name == AddedColumn);
            return null;
        }
        Assert.True(designer.StatusMessage?.Contains("successfully") == true, designer.StatusMessage);
        var after = await source.GetTableDesignAsync(session.ConnectionString, identity);
        Assert.Contains(after.Columns, c => c.Name == AddedColumn);
        var expected = lines(before);
        var actual = lines(after).Where(l => !l.StartsWith($"column {AddedColumn} ", StringComparison.Ordinal)).ToList();
        var lost = expected.Except(actual).Select(l => "lost: " + l).Concat(actual.Except(expected).Select(l => "changed to: " + l)).ToList();
        if (lost.Count > 0) lost.Add("saved with: " + preview);
        return lost;
    }
}
