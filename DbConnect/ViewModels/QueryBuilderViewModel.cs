using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class QueryBuilderViewModel : ViewModelBase
{
    private static readonly string[] JoinTypeCycle = ["INNER", "LEFT", "RIGHT", "FULL OUTER", "CROSS"];

    public override string Title => "Query Builder";

    [ObservableProperty] private ObservableCollection<QueryBuilderTable> _tables = new();
    [ObservableProperty] private ObservableCollection<QueryBuilderJoin> _joins = new();
    [ObservableProperty] private DatabaseSession? _session;
    [ObservableProperty] private string _generatedSql = string.Empty;
    [ObservableProperty] private bool _showCanvas = true;
    [ObservableProperty] private bool _isDrawJoinMode;

    public ObservableCollection<DatabaseSession> AvailableSessions { get; } = new();
    public ObservableCollection<QueryBuilderTable> TableCards => Tables;
    public ObservableCollection<WhereCondition> WhereConditions { get; } = new();
    public ObservableCollection<GroupByColumn> GroupByColumns { get; } = new();
    public ObservableCollection<OrderByColumn> OrderByColumns { get; } = new();
    public ObservableCollection<HavingCondition> HavingConditions { get; } = new();
    public Action<string>? OpenInEditorCallback { get; set; }

    public QueryBuilderViewModel()
    {
        Tables.CollectionChanged += (_, _) => GenerateSql();
        Joins.CollectionChanged += (_, _) => GenerateSql();
        WhereConditions.CollectionChanged += (_, _) => GenerateSql();
        GroupByColumns.CollectionChanged += (_, _) => GenerateSql();
        OrderByColumns.CollectionChanged += (_, _) => GenerateSql();
        HavingConditions.CollectionChanged += (_, _) => GenerateSql();
        GenerateSql();
    }

    public QueryBuilderTable AddTableToCanvas(string tableName, string? alias = null)
    {
        var existing = Tables.FirstOrDefault(t =>
            string.Equals(t.TableName, tableName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(t.Alias, alias ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            return existing;

        var table = new QueryBuilderTable
        {
            TableName = tableName,
            Alias = alias ?? string.Empty,
            CanvasX = 50 + (Tables.Count * 230),
            CanvasY = 50
        };

        Tables.Add(table);
        return table;
    }

    public async Task<QueryBuilderTable> AddTableToCanvasAsync(string tableName, string? alias = null)
    {
        var table = AddTableToCanvas(tableName, alias);
        await LoadSchemaForTableAsync(tableName);
        await TryAutoCreateForeignKeyJoinsAsync(table);
        GenerateSql();
        return table;
    }

    [RelayCommand]
    private void AddTable(QueryBuilderTable table)
    {
        if (table.CanvasX <= 0 && table.CanvasY <= 0)
        {
            table.CanvasX = 50 + (Tables.Count * 230);
            table.CanvasY = 50;
        }

        Tables.Add(table);
    }

    [RelayCommand]
    private void RemoveTable(QueryBuilderTable? table)
    {
        if (table == null)
            return;

        Tables.Remove(table);

        var tableKeys = new[] { table.TableName, ResolveAlias(table) };
        var toRemove = Joins.Where(j =>
                tableKeys.Contains(j.LeftTableAlias, StringComparer.OrdinalIgnoreCase) ||
                tableKeys.Contains(j.RightTableAlias, StringComparer.OrdinalIgnoreCase) ||
                tableKeys.Contains(j.LeftTableName, StringComparer.OrdinalIgnoreCase) ||
                tableKeys.Contains(j.RightTableName, StringComparer.OrdinalIgnoreCase))
            .ToList();

        foreach (var join in toRemove)
            Joins.Remove(join);
    }

    public void RemoveTableCard(QueryBuilderTable? card) => RemoveTable(card);

    public void AddJoin(JoinDefinition join)
    {
        var leftTable = Tables.FirstOrDefault(t => string.Equals(t.TableName, join.LeftTable, StringComparison.OrdinalIgnoreCase) ||
                                                   string.Equals(ResolveAlias(t), join.LeftTable, StringComparison.OrdinalIgnoreCase));
        var rightTable = Tables.FirstOrDefault(t => string.Equals(t.TableName, join.RightTable, StringComparison.OrdinalIgnoreCase) ||
                                                    string.Equals(ResolveAlias(t), join.RightTable, StringComparison.OrdinalIgnoreCase));

        var queryJoin = new QueryBuilderJoin
        {
            LeftTableName = leftTable?.TableName ?? join.LeftTable,
            LeftTableAlias = leftTable != null ? ResolveAlias(leftTable) : join.LeftTable,
            LeftColumn = join.LeftColumn,
            RightTableName = rightTable?.TableName ?? join.RightTable,
            RightTableAlias = rightTable != null ? ResolveAlias(rightTable) : join.RightTable,
            RightColumn = join.RightColumn,
            JoinType = string.IsNullOrWhiteSpace(join.JoinType) ? "INNER" : join.JoinType
        };

        if (Joins.Any(existing =>
                string.Equals(existing.LeftTableAlias, queryJoin.LeftTableAlias, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.LeftColumn, queryJoin.LeftColumn, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.RightTableAlias, queryJoin.RightTableAlias, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.RightColumn, queryJoin.RightColumn, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Joins.Add(queryJoin);
    }

    [RelayCommand]
    private void RemoveJoin(QueryBuilderJoin? join)
    {
        if (join != null)
            Joins.Remove(join);
    }

    public void ChangeJoinType(QueryBuilderJoin join)
    {
        var currentIndex = Array.FindIndex(JoinTypeCycle, jt => string.Equals(jt, join.JoinType, StringComparison.OrdinalIgnoreCase));
        join.JoinType = JoinTypeCycle[(currentIndex + 1 + JoinTypeCycle.Length) % JoinTypeCycle.Length];
        GenerateSql();
    }

    public void SetJoinType(QueryBuilderJoin join, string joinType)
    {
        join.JoinType = joinType;
        GenerateSql();
    }

    [RelayCommand]
    private void OpenInEditor()
    {
        if (!string.IsNullOrWhiteSpace(GeneratedSql))
            OpenInEditorCallback?.Invoke(GeneratedSql);
    }

    [RelayCommand]
    public async Task LoadSchemaForTableAsync(string tableName)
    {
        if (Session == null)
            return;

        var existing = Tables.FirstOrDefault(t => string.Equals(t.TableName, tableName, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
            return;

        var cols = await Session.GetColumnDefinitionsAsync(tableName);
        existing.Columns.Clear();
        foreach (var col in cols)
            existing.Columns.Add(new QueryBuilderColumn { ColumnName = col.Name });

        GenerateSql();
    }

    public async Task TryAutoCreateForeignKeyJoinsAsync(QueryBuilderTable addedTable)
    {
        if (Session == null || Tables.Count < 2)
            return;

        var fkNodes = await Session.GetObjectsAsync(addedTable.TableName, SchemaNodeType.ForeignKey);
        foreach (var fkNode in fkNodes)
        {
            var parsed = ParseForeignKeyNode(fkNode);
            if (parsed == null)
                continue;

            var targetTable = Tables.FirstOrDefault(t => string.Equals(t.TableName, parsed.Value.TargetTable, StringComparison.OrdinalIgnoreCase));
            if (targetTable == null)
                continue;

            AddJoin(new JoinDefinition
            {
                LeftTable = addedTable.TableName,
                LeftColumn = parsed.Value.SourceColumn,
                RightTable = targetTable.TableName,
                RightColumn = parsed.Value.TargetColumn,
                JoinType = "INNER"
            });
        }
    }

    public void GenerateSql()
    {
        GeneratedSql = BuildSelectStatement();
    }

    public string BuildSelectStatement()
    {
        if (Tables.Count == 0)
            return string.Empty;

        var selectedCols = Tables
            .SelectMany(table => table.Columns
                .Where(column => column.IsSelected)
                .Select(column =>
                {
                    var tablePrefix = ResolveAlias(table);
                    var columnExpression = $"{tablePrefix}.{column.ColumnName}";
                    return string.IsNullOrWhiteSpace(column.AliasAs)
                        ? columnExpression
                        : $"{columnExpression} AS {column.AliasAs}";
                }))
            .ToList();

        if (selectedCols.Count == 0)
            selectedCols = Tables.Select(table => $"{ResolveAlias(table)}.*").ToList();

        var sb = new StringBuilder();
        sb.Append("SELECT ");
        sb.AppendLine(string.Join(",\n       ", selectedCols));
        sb.Append($"FROM {FormatTableReference(Tables[0])}");

        foreach (var join in Joins)
        {
            var rightTableName = string.IsNullOrWhiteSpace(join.RightTableName)
                ? ResolveTableName(join.RightTableAlias)
                : join.RightTableName;
            var rightAliasSql = BuildAliasSuffix(rightTableName, join.RightTableAlias);
            var leftPrefix = string.IsNullOrWhiteSpace(join.LeftTableAlias) ? join.LeftTableName : join.LeftTableAlias;
            var rightPrefix = string.IsNullOrWhiteSpace(join.RightTableAlias) ? rightTableName : join.RightTableAlias;

            sb.AppendLine();
            sb.Append($"{join.JoinType} JOIN {rightTableName}{rightAliasSql} ON {leftPrefix}.{join.LeftColumn} = {rightPrefix}.{join.RightColumn}");
        }

        var whereClauses = Tables
            .SelectMany(table => table.Columns
                .Where(column => !string.IsNullOrWhiteSpace(column.WhereExpression))
                .Select(column => $"{ResolveAlias(table)}.{column.ColumnName} {column.WhereExpression}"))
            .Concat(WhereConditions.Where(condition => !string.IsNullOrWhiteSpace(condition.Expression))
                .Select(condition => condition.Expression.Trim()))
            .ToList();

        if (whereClauses.Count > 0)
        {
            sb.AppendLine();
            sb.Append("WHERE ");
            sb.Append(string.Join("\n  AND ", whereClauses));
        }

        if (GroupByColumns.Count > 0)
        {
            sb.AppendLine();
            sb.Append("GROUP BY ");
            sb.Append(string.Join(", ", GroupByColumns.Where(column => !string.IsNullOrWhiteSpace(column.Column)).Select(column => column.Column)));
        }

        if (HavingConditions.Count > 0)
        {
            sb.AppendLine();
            sb.Append("HAVING ");
            sb.Append(string.Join("\n   AND ", HavingConditions.Where(condition => !string.IsNullOrWhiteSpace(condition.Expression)).Select(condition => condition.Expression.Trim())));
        }

        if (OrderByColumns.Count > 0)
        {
            sb.AppendLine();
            sb.Append("ORDER BY ");
            sb.Append(string.Join(", ", OrderByColumns.Where(column => !string.IsNullOrWhiteSpace(column.Column))
                .Select(column => $"{column.Column} {(column.Descending ? "DESC" : "ASC")}")));
        }

        return sb.ToString().TrimEnd();
    }

    private static string ResolveAlias(QueryBuilderTable table) =>
        string.IsNullOrWhiteSpace(table.Alias) ? table.TableName : table.Alias;

    private string ResolveTableName(string aliasOrTableName)
    {
        var table = Tables.FirstOrDefault(candidate =>
            string.Equals(candidate.TableName, aliasOrTableName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.Alias, aliasOrTableName, StringComparison.OrdinalIgnoreCase));
        return table?.TableName ?? aliasOrTableName;
    }

    private static string FormatTableReference(QueryBuilderTable table) =>
        $"{table.TableName}{BuildAliasSuffix(table.TableName, ResolveAlias(table))}";

    private static string BuildAliasSuffix(string tableName, string aliasOrTableName) =>
        string.Equals(tableName, aliasOrTableName, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $" {aliasOrTableName}";

    private static (string SourceColumn, string TargetTable, string TargetColumn)? ParseForeignKeyNode(SchemaNode fkNode)
    {
        if (string.IsNullOrWhiteSpace(fkNode.Name) || !fkNode.Name.Contains('→'))
            return null;

        var parts = fkNode.Name.Split('→', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return null;

        var sourceColumn = string.IsNullOrWhiteSpace(fkNode.RawName) ? parts[0].Trim() : fkNode.RawName;
        var targetPart = parts[1].Trim();
        var dotIndex = targetPart.LastIndexOf('.');
        if (dotIndex > 0 && dotIndex < targetPart.Length - 1)
            return (sourceColumn, targetPart[..dotIndex].Trim(), targetPart[(dotIndex + 1)..].Trim());

        return (sourceColumn, targetPart, "id");
    }
}
