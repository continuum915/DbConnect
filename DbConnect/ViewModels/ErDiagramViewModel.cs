using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class ErDiagramViewModel : ViewModelBase
{
    private const double DefaultTableWidth = 220;
    private const double DefaultTableHeight = 120;
    private readonly ErDiagramLayoutService _layoutService = new();
    private readonly DiagramWorkspaceService _workspaceService = new();
    private readonly VirtualSchemaService _virtualSchemaService = new();
    private readonly SchemaDiffService _schemaDiffService = new();

    private string? _relationshipStartTable;
    private string? _relationshipStartColumn;
    public static readonly IReadOnlyDictionary<string, string> ColorGroups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Red"] = "#F8D7DA",
        ["Blue"] = "#D9ECFF",
        ["Green"] = "#D9F2E6",
        ["Yellow"] = "#FFF3BF",
        ["Purple"] = "#E6D9F8",
        ["Orange"] = "#FFE2C7"
    };

    public override string Title => "ER Diagram";

    [ObservableProperty] private DatabaseSession? _session;
    [ObservableProperty] private ErDiagramModel? _model;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private DiagramWorkspace? _currentWorkspace;
    [ObservableProperty] private bool _isDesignMode;
    [ObservableProperty] private bool _isRelationshipMode;
    [ObservableProperty] private VirtualSchema _virtualSchema = new();
    [ObservableProperty] private bool _hasUnsavedModelChanges;
    [ObservableProperty] private string _forwardEngineeringScript = string.Empty;
    [ObservableProperty] private bool _showForwardEngineeringScript;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private VirtualTable? _editingVirtualTable;
    [ObservableProperty] private ColumnDefinition? _selectedEditingColumn;
    [ObservableProperty] private bool _isColumnEditorOpen;
    [ObservableProperty] private double _columnEditorX;
    [ObservableProperty] private double _columnEditorY;

    public ObservableCollection<ErTable> VisibleTables { get; } = new();
    public ObservableCollection<string> HiddenTables { get; } = new();
    public ObservableCollection<DiagramNote> Notes { get; } = new();
    public ObservableCollection<ErRelationshipVisual> LiveRelationshipVisuals { get; } = new();
    public ObservableCollection<ErRelationshipVisual> VirtualRelationshipVisuals { get; } = new();
    public ObservableCollection<ErRelationship> DisplayRelationships { get; } = new();
    public ObservableCollection<ColumnDefinition> EditingColumns { get; } = new();
    public ObservableCollection<DatabaseSession> AvailableSessions { get; } = new();

    public Action<SchemaDiffViewModel>? RequestOpenDiffTab { get; set; }

    partial void OnSearchTextChanged(string value) => RefreshCanvas();

    partial void OnModelChanged(ErDiagramModel? value) => RefreshCanvas();

    partial void OnVirtualSchemaChanged(VirtualSchema value) => RefreshCanvas();

    partial void OnSessionChanged(DatabaseSession? value)
    {
        if (value != null)
            _ = LoadDiagram();
    }

    partial void OnIsDesignModeChanged(bool value)
    {
        if (value)
            return;

        IsRelationshipMode = false;
        ClearRelationshipDraft();
        CloseColumnEditor();
    }

    partial void OnIsRelationshipModeChanged(bool value)
    {
        if (!value)
            ClearRelationshipDraft();
    }

    public void SetAvailableSessions(IEnumerable<DatabaseSession> sessions)
    {
        AvailableSessions.Clear();
        foreach (var session in sessions)
            AvailableSessions.Add(session);
    }

    [RelayCommand]
    public void ToggleDesignMode() => IsDesignMode = !IsDesignMode;

    [RelayCommand]
    public void ToggleRelationshipMode() => IsRelationshipMode = !IsRelationshipMode;

    [RelayCommand]
    public void HideTable(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            return;

        if (!HiddenTables.Contains(tableName))
            HiddenTables.Add(tableName);

        HasUnsavedModelChanges = true;
        RefreshCanvas();
    }

    [RelayCommand]
    public void ShowTable(string tableName)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            return;

        HiddenTables.Remove(tableName);
        HasUnsavedModelChanges = true;
        RefreshCanvas();
    }

    [RelayCommand]
    public void SetTableColorGroup(object? param)
    {
        var selection = param switch
        {
            ValueTuple<string, string?> tuple => (tuple.Item1, tuple.Item2),
            Tuple<string, string?> tuple => (tuple.Item1, tuple.Item2),
            string[] values when values.Length >= 2 => (values[0], string.Equals(values[1], "None", StringComparison.OrdinalIgnoreCase) ? null : values[1]),
            _ => ((string TableName, string? ColorGroup)?)null
        };

        if (selection is null)
            return;

        var colorHex = ResolveColorGroupHex(selection.Value.ColorGroup);
        var liveTable = Model?.Tables.FirstOrDefault(t => string.Equals(t.TableName, selection.Value.TableName, StringComparison.OrdinalIgnoreCase));
        if (liveTable != null)
        {
            liveTable.ColorGroup = selection.Value.ColorGroup;
            liveTable.ColorHex = colorHex;
        }

        UpsertWorkspaceLayout(selection.Value.TableName, layout =>
        {
            layout.ColorGroup = selection.Value.ColorGroup;
            layout.ColorHex = colorHex;
        });

        HasUnsavedModelChanges = true;
        RefreshCanvas();
    }

    [RelayCommand]
    public void AddNote()
    {
        Notes.Add(new DiagramNote { Text = "Note", X = 100, Y = 100 });
        HasUnsavedModelChanges = true;
    }

    public void AddNoteAt(double x, double y)
    {
        Notes.Add(new DiagramNote { Text = "Note", X = x, Y = y });
        HasUnsavedModelChanges = true;
    }

    public void UpdateTablePosition(ErTable table, double x, double y)
    {
        var virtualTable = FindVirtualTable(table.TableName);
        if (virtualTable != null)
        {
            virtualTable.X = x;
            virtualTable.Y = y;
            HasUnsavedModelChanges = true;
        }

        var liveTable = Model?.Tables.FirstOrDefault(t => string.Equals(t.TableName, table.TableName, StringComparison.OrdinalIgnoreCase));
        if (liveTable != null)
        {
            liveTable.X = x;
            liveTable.Y = y;
        }

        UpsertWorkspaceLayout(table.TableName, layout =>
        {
            layout.X = x;
            layout.Y = y;
        });

        RefreshRelationshipVisuals();
    }

    public void UpdateNotePosition(DiagramNote note, double x, double y)
    {
        note.X = x;
        note.Y = y;
        HasUnsavedModelChanges = true;
    }

    [RelayCommand]
    public async Task LoadDiagram()
    {
        if (Session == null)
            return;

        IsLoading = true;
        try
        {
            Model = await _layoutService.BuildErModelAsync(Session);
            await LoadWorkspaceAsync(Session.Connection.Name, Session.DatabaseLabel ?? Session.Name);
            StatusMessage = $"Loaded ER diagram for {Session.DisplayName}.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task SaveWorkspaceAsync()
    {
        if (Session == null)
            return;

        var workspace = CaptureWorkspace();
        workspace.ConnectionName = Session.Connection.Name;
        workspace.DatabaseName = Session.DatabaseLabel ?? Session.Name;
        CurrentWorkspace = workspace;
        await _workspaceService.SaveAsync(workspace);
    }

    public async Task LoadWorkspaceAsync(string connectionName, string databaseName)
    {
        CurrentWorkspace = await _workspaceService.LoadAsync(connectionName, databaseName)
            ?? new DiagramWorkspace { ConnectionName = connectionName, DatabaseName = databaseName };

        ApplyWorkspace(CurrentWorkspace);
    }

    [RelayCommand]
    public void AddVirtualTable()
    {
        var offset = VirtualSchema.Tables.Count;
        AddVirtualTableAt(50 + offset * 30, 50 + offset * 20, true);
    }

    public VirtualTable AddVirtualTableAt(double x, double y, bool openEditor = false)
    {
        var table = new VirtualTable
        {
            Name = MakeUniqueTableName($"table_{VirtualSchema.Tables.Count + 1}"),
            X = Math.Max(0, x),
            Y = Math.Max(0, y),
            Columns =
            [
                new ColumnDefinition { Name = "id", DataType = "INTEGER", IsPrimaryKey = true, IsNullable = false }
            ]
        };

        VirtualSchema.Tables.Add(table);
        HasUnsavedModelChanges = true;
        RefreshCanvas();

        if (openEditor)
            StartEditing(table);

        return table;
    }

    public async Task OpenColumnEditorAsync(ErTable table)
    {
        if (!IsDesignMode)
            return;

        var virtualTable = await EnsureVirtualTableAsync(table);
        StartEditing(virtualTable);
    }

    public void BeginRelationship(string tableName, string columnName)
    {
        if (!IsDesignMode || !IsRelationshipMode)
            return;

        _relationshipStartTable = tableName;
        _relationshipStartColumn = columnName;
        StatusMessage = $"Drawing relationship from {tableName}.{columnName}…";
    }

    public void CompleteRelationship(string tableName, string columnName)
    {
        if (!IsDesignMode || !IsRelationshipMode || string.IsNullOrWhiteSpace(_relationshipStartTable) || string.IsNullOrWhiteSpace(_relationshipStartColumn))
            return;

        if (string.Equals(_relationshipStartTable, tableName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_relationshipStartColumn, columnName, StringComparison.OrdinalIgnoreCase))
        {
            ClearRelationshipDraft();
            return;
        }

        var fromTable = EnsureVirtualTableForDesign(_relationshipStartTable!);
        var toTable = EnsureVirtualTableForDesign(tableName);
        if (fromTable == null || toTable == null)
        {
            StatusMessage = "Unable to resolve relationship endpoints.";
            ClearRelationshipDraft();
            return;
        }

        if (!fromTable.Columns.Any(column => string.Equals(column.Name, _relationshipStartColumn, StringComparison.OrdinalIgnoreCase))
            || !toTable.Columns.Any(column => string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = "Relationships must start and end on visible columns.";
            ClearRelationshipDraft();
            return;
        }

        var alreadyExists = VirtualSchema.Relationships.Any(rel =>
            string.Equals(rel.FromTable, fromTable.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(rel.FromColumn, _relationshipStartColumn, StringComparison.OrdinalIgnoreCase)
            && string.Equals(rel.ToTable, toTable.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(rel.ToColumn, columnName, StringComparison.OrdinalIgnoreCase));

        if (!alreadyExists)
        {
            VirtualSchema.Relationships.Add(new VirtualRelationship
            {
                Name = $"fk_{fromTable.Name}_{_relationshipStartColumn}",
                FromTable = fromTable.Name,
                FromColumn = _relationshipStartColumn!,
                ToTable = toTable.Name,
                ToColumn = columnName
            });

            HasUnsavedModelChanges = true;
            RefreshRelationshipVisuals();
            RefreshRelationshipList();
            StatusMessage = $"Added relationship {fromTable.Name}.{_relationshipStartColumn} → {toTable.Name}.{columnName}.";
        }
        else
        {
            StatusMessage = "That relationship already exists in the virtual model.";
        }

        ClearRelationshipDraft();
    }

    [RelayCommand]
    public void AddEditingColumn()
    {
        EditingColumns.Add(new ColumnDefinition
        {
            Name = MakeUniqueColumnName("column"),
            DataType = "TEXT",
            IsNullable = true
        });
        HasUnsavedModelChanges = true;
    }

    [RelayCommand]
    public void RemoveSelectedColumn()
    {
        if (SelectedEditingColumn == null)
            return;

        EditingColumns.Remove(SelectedEditingColumn);
        SelectedEditingColumn = null;
        HasUnsavedModelChanges = true;
    }

    [RelayCommand]
    public void ApplyColumnEditor()
    {
        if (EditingVirtualTable == null)
            return;

        var originalTableName = EditingVirtualTable.Name;
        EditingVirtualTable.Name = EnsureUniqueTableName(EditingVirtualTable.Name, EditingVirtualTable);

        var renamedColumns = EditingColumns
            .Where(column => !string.IsNullOrWhiteSpace(column.Name))
            .Select(column => (OriginalName: column.Original?.Name, Updated: column.Clone()))
            .ToList();

        EditingVirtualTable.Columns = renamedColumns
            .Select(item => item.Updated)
            .ToList();

        if (EditingVirtualTable.Columns.Count == 0)
        {
            EditingVirtualTable.Columns.Add(new ColumnDefinition
            {
                Name = "id",
                DataType = "INTEGER",
                IsPrimaryKey = true,
                IsNullable = false
            });
        }

        if (!string.Equals(originalTableName, EditingVirtualTable.Name, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var relationship in VirtualSchema.Relationships)
            {
                if (string.Equals(relationship.FromTable, originalTableName, StringComparison.OrdinalIgnoreCase))
                    relationship.FromTable = EditingVirtualTable.Name;
                if (string.Equals(relationship.ToTable, originalTableName, StringComparison.OrdinalIgnoreCase))
                    relationship.ToTable = EditingVirtualTable.Name;
            }
        }

        foreach (var renamedColumn in renamedColumns)
        {
            if (string.IsNullOrWhiteSpace(renamedColumn.OriginalName)
                || string.Equals(renamedColumn.OriginalName, renamedColumn.Updated.Name, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var relationship in VirtualSchema.Relationships)
            {
                if (string.Equals(relationship.FromTable, EditingVirtualTable.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(relationship.FromColumn, renamedColumn.OriginalName, StringComparison.OrdinalIgnoreCase))
                {
                    relationship.FromColumn = renamedColumn.Updated.Name;
                }

                if (string.Equals(relationship.ToTable, EditingVirtualTable.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(relationship.ToColumn, renamedColumn.OriginalName, StringComparison.OrdinalIgnoreCase))
                {
                    relationship.ToColumn = renamedColumn.Updated.Name;
                }
            }
        }

        HasUnsavedModelChanges = true;
        CloseColumnEditor();
        RefreshCanvas();
    }

    [RelayCommand]
    public void CancelColumnEditor() => CloseColumnEditor();

    public async Task SaveModelAsync(string path)
    {
        var workspace = CaptureWorkspace();
        await _virtualSchemaService.SaveModelAsync(VirtualSchema, workspace, path);
        HasUnsavedModelChanges = false;
        StatusMessage = $"Model saved to {path}";
    }

    public async Task OpenModelAsync(string path)
    {
        var document = await _virtualSchemaService.LoadModelAsync(path);
        if (document == null)
        {
            StatusMessage = $"Unable to load model from {path}.";
            return;
        }

        VirtualSchema = document.Schema ?? new VirtualSchema();
        CurrentWorkspace = document.Workspace ?? new DiagramWorkspace();
        ApplyWorkspace(CurrentWorkspace);
        ShowForwardEngineeringScript = false;
        ForwardEngineeringScript = string.Empty;
        HasUnsavedModelChanges = false;
        StatusMessage = $"Loaded model from {path}.";
    }

    [RelayCommand]
    public async Task SyncToDatabaseAsync()
    {
        if (Session == null)
        {
            StatusMessage = "Connect to a database before syncing the virtual schema.";
            return;
        }

        if (VirtualSchema.Tables.Count == 0 && VirtualSchema.Relationships.Count == 0)
        {
            StatusMessage = "No virtual schema changes to sync.";
            return;
        }

        if (!Session.Capabilities.SupportsForwardEngineering)
        {
            StatusMessage = "Forward engineering currently supports PostgreSQL, SQL Server, MySQL, and SQLite.";
            return;
        }

        var diffItems = (await _schemaDiffService.DiffVirtualSchemaAsync(VirtualSchema, Session)).ToList();
        ForwardEngineeringScript = BuildForwardEngineeringScript(diffItems);
        ShowForwardEngineeringScript = !string.IsNullOrWhiteSpace(ForwardEngineeringScript);

        if (diffItems.Count == 0)
        {
            StatusMessage = "Virtual schema already matches the live database.";
            return;
        }

        if (RequestOpenDiffTab != null)
        {
            var diffViewModel = new SchemaDiffViewModel(AvailableSessions);
            diffViewModel.LoadPrecomputedDiff(diffItems, ForwardEngineeringScript, $"Forward engineering ready. {diffItems.Count} change(s) detected.");
            RequestOpenDiffTab(diffViewModel);
        }

        StatusMessage = $"Forward engineering diff ready ({diffItems.Count} change(s)).";
    }

    private DiagramWorkspace CaptureWorkspace()
    {
        var workspace = new DiagramWorkspace
        {
            ConnectionName = Session?.Connection.Name ?? VirtualSchema.Name,
            DatabaseName = Session?.DatabaseLabel ?? VirtualSchema.Name,
            HiddenTables = HiddenTables.ToList(),
            Notes = Notes.Select(note => new DiagramNote
            {
                Text = note.Text,
                X = note.X,
                Y = note.Y,
                Width = note.Width
            }).ToList(),
            TableLayouts = VisibleTables.Select(table => new TableNodeLayout
            {
                TableName = table.TableName,
                X = table.X,
                Y = table.Y,
                ColorHex = ResolveTableColor(table.TableName),
                ColorGroup = ResolveTableColorGroup(table.TableName)
            }).ToList()
        };

        return workspace;
    }

    private void ApplyWorkspace(DiagramWorkspace workspace)
    {
        HiddenTables.Clear();
        foreach (var hiddenTable in workspace.HiddenTables)
            HiddenTables.Add(hiddenTable);

        Notes.Clear();
        foreach (var note in workspace.Notes)
        {
            Notes.Add(new DiagramNote
            {
                Text = note.Text,
                X = note.X,
                Y = note.Y,
                Width = note.Width
            });
        }

        ApplySavedPositions(workspace.TableLayouts);
        RefreshCanvas();
    }

    private void RefreshCanvas()
    {
        RefreshVisibleTables();
        RefreshRelationshipList();
        RefreshRelationshipVisuals();
    }

    private void RefreshVisibleTables()
    {
        VisibleTables.Clear();

        var search = SearchText?.Trim() ?? string.Empty;
        var virtualLookup = VirtualSchema.Tables.ToDictionary(table => table.Name, StringComparer.OrdinalIgnoreCase);

        if (Model != null)
        {
            foreach (var table in Model.Tables.Where(t => !virtualLookup.ContainsKey(t.TableName)))
            {
                if (HiddenTables.Contains(table.TableName))
                    continue;

                if (!string.IsNullOrWhiteSpace(search)
                    && !table.TableName.Contains(search, StringComparison.OrdinalIgnoreCase))
                    continue;

                VisibleTables.Add(table);
            }
        }

        foreach (var virtualTable in VirtualSchema.Tables)
        {
            if (HiddenTables.Contains(virtualTable.Name))
                continue;

            if (!string.IsNullOrWhiteSpace(search)
                && !virtualTable.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                continue;

            VisibleTables.Add(ToErTable(virtualTable));
        }
    }

    private void RefreshRelationshipList()
    {
        DisplayRelationships.Clear();

        if (Model != null)
        {
            foreach (var relationship in Model.Relationships)
                DisplayRelationships.Add(relationship);
        }

        foreach (var relationship in VirtualSchema.Relationships)
        {
            DisplayRelationships.Add(new ErRelationship
            {
                FromTable = relationship.FromTable,
                FromColumn = relationship.FromColumn,
                ToTable = relationship.ToTable,
                ToColumn = relationship.ToColumn
            });
        }
    }

    private void RefreshRelationshipVisuals()
    {
        LiveRelationshipVisuals.Clear();
        VirtualRelationshipVisuals.Clear();

        if (Model != null)
        {
            foreach (var relationship in Model.Relationships)
            {
                var visual = ErRelationshipVisualFactory.CreateRelationshipVisual(VisibleTables, relationship.FromTable, relationship.FromColumn, relationship.ToTable, relationship.ToColumn, relationship.FromTable);
                if (visual != null)
                    LiveRelationshipVisuals.Add(visual);
            }
        }

        foreach (var relationship in VirtualSchema.Relationships)
        {
            var visual = ErRelationshipVisualFactory.CreateRelationshipVisual(VisibleTables, relationship.FromTable, relationship.FromColumn, relationship.ToTable, relationship.ToColumn, string.IsNullOrWhiteSpace(relationship.Name) ? relationship.FromTable : relationship.Name);
            if (visual != null)
                VirtualRelationshipVisuals.Add(visual);
        }
    }

    private async Task<VirtualTable> EnsureVirtualTableAsync(ErTable table)
    {
        var existing = FindVirtualTable(table.TableName);
        if (existing != null)
            return existing;

        var columns = new List<ColumnDefinition>();
        if (Session != null && Model?.Tables.Any(t => string.Equals(t.TableName, table.TableName, StringComparison.OrdinalIgnoreCase)) == true)
        {
            try
            {
                columns = await Session.GetColumnDefinitionsAsync(table.TableName);
            }
            catch
            {
                columns = new List<ColumnDefinition>();
            }
        }

        if (columns.Count == 0)
        {
            columns = table.Columns.Select(column => new ColumnDefinition
            {
                Name = column.Name,
                DataType = string.IsNullOrWhiteSpace(column.DataType) ? "TEXT" : column.DataType,
                IsPrimaryKey = column.IsPrimaryKey,
                IsNullable = column.IsNullable
            }).ToList();
        }

        var virtualTable = new VirtualTable
        {
            Name = table.TableName,
            X = table.X,
            Y = table.Y,
            Columns = columns.Select(column => column.Clone()).ToList()
        };

        VirtualSchema.Tables.Add(virtualTable);
        HasUnsavedModelChanges = true;
        RefreshCanvas();
        return virtualTable;
    }

    private VirtualTable? EnsureVirtualTableForDesign(string tableName)
    {
        var existing = FindVirtualTable(tableName);
        if (existing != null)
            return existing;

        var source = VisibleTables.FirstOrDefault(table => string.Equals(table.TableName, tableName, StringComparison.OrdinalIgnoreCase));
        if (source == null)
            return null;

        var virtualTable = new VirtualTable
        {
            Name = source.TableName,
            X = source.X,
            Y = source.Y,
            Columns = source.Columns.Select(column => new ColumnDefinition
            {
                Name = column.Name,
                DataType = string.IsNullOrWhiteSpace(column.DataType) ? "TEXT" : column.DataType,
                IsPrimaryKey = column.IsPrimaryKey,
                IsNullable = column.IsNullable
            }).ToList()
        };

        VirtualSchema.Tables.Add(virtualTable);
        HasUnsavedModelChanges = true;
        RefreshCanvas();
        return virtualTable;
    }

    private void StartEditing(VirtualTable table)
    {
        EditingVirtualTable = table;
        EditingColumns.Clear();
        foreach (var column in table.Columns.DefaultIfEmpty(new ColumnDefinition
        {
            Name = "id",
            DataType = "INTEGER",
            IsPrimaryKey = true,
            IsNullable = false
        }))
        {
            var editableColumn = column.Clone();
            editableColumn.Original = column.Clone();
            EditingColumns.Add(editableColumn);
        }

        ColumnEditorX = table.X + DefaultTableWidth + 16;
        ColumnEditorY = table.Y;
        IsColumnEditorOpen = true;
    }

    private void CloseColumnEditor()
    {
        IsColumnEditorOpen = false;
        EditingVirtualTable = null;
        SelectedEditingColumn = null;
        EditingColumns.Clear();
    }

    private void ApplySavedPositions(List<TableNodeLayout> layouts)
    {
        if (layouts.Count == 0)
            return;

        var layoutLookup = layouts.ToDictionary(layout => layout.TableName, StringComparer.OrdinalIgnoreCase);

        if (Model != null)
        {
            foreach (var table in Model.Tables)
            {
                if (!layoutLookup.TryGetValue(table.TableName, out var layout))
                    continue;

                table.X = layout.X;
                table.Y = layout.Y;
                table.ColorGroup = layout.ColorGroup;
                table.ColorHex = !string.IsNullOrWhiteSpace(layout.ColorGroup)
                    ? ResolveColorGroupHex(layout.ColorGroup)
                    : layout.ColorHex;
            }
        }

        foreach (var table in VirtualSchema.Tables)
        {
            if (!layoutLookup.TryGetValue(table.Name, out var layout))
                continue;

            table.X = layout.X;
            table.Y = layout.Y;
        }
    }

    private string BuildForwardEngineeringScript(IReadOnlyList<SchemaDiffItem> diffItems)
    {
        var body = string.Join(
            Environment.NewLine + Environment.NewLine,
            diffItems
                .Where(item => !string.IsNullOrWhiteSpace(item.GeneratedScript))
                .Select(item => item.GeneratedScript.TrimEnd()));

        if (string.IsNullOrWhiteSpace(body) && Session != null)
            body = _virtualSchemaService.GenerateDdl(VirtualSchema, Session.ProviderName);

        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;

        return string.Join(Environment.NewLine,
        [
            $"-- Forward engineering script for {VirtualSchema.Name}",
            $"-- Generated: {DateTime.UtcNow:u}",
            string.Empty,
            body
        ]);
    }

    private string ResolveTableColor(string tableName)
    {
        var colorGroup = ResolveTableColorGroup(tableName);
        var groupedColor = ResolveColorGroupHex(colorGroup);
        if (!string.IsNullOrWhiteSpace(groupedColor))
            return groupedColor;

        var liveColor = Model?.Tables.FirstOrDefault(table => string.Equals(table.TableName, tableName, StringComparison.OrdinalIgnoreCase))?.ColorHex;
        if (!string.IsNullOrWhiteSpace(liveColor))
            return liveColor;

        return CurrentWorkspace?.TableLayouts.FirstOrDefault(layout => string.Equals(layout.TableName, tableName, StringComparison.OrdinalIgnoreCase))?.ColorHex
            ?? "#FFFFFF";
    }

    public string? ResolveTableColorGroup(string tableName)
    {
        var liveGroup = Model?.Tables.FirstOrDefault(table => string.Equals(table.TableName, tableName, StringComparison.OrdinalIgnoreCase))?.ColorGroup;
        if (!string.IsNullOrWhiteSpace(liveGroup))
            return liveGroup;

        return CurrentWorkspace?.TableLayouts.FirstOrDefault(layout => string.Equals(layout.TableName, tableName, StringComparison.OrdinalIgnoreCase))?.ColorGroup;
    }

    public string? ResolveColorGroupHex(string? colorGroup)
    {
        if (string.IsNullOrWhiteSpace(colorGroup))
            return null;

        return ColorGroups.TryGetValue(colorGroup, out var colorHex) ? colorHex : null;
    }

    private void UpsertWorkspaceLayout(string tableName, Action<TableNodeLayout> update)
    {
        CurrentWorkspace ??= new DiagramWorkspace();
        var layout = CurrentWorkspace.TableLayouts.FirstOrDefault(item => string.Equals(item.TableName, tableName, StringComparison.OrdinalIgnoreCase));
        if (layout == null)
        {
            layout = new TableNodeLayout { TableName = tableName };
            CurrentWorkspace.TableLayouts.Add(layout);
        }

        update(layout);
    }

    private ErTable ToErTable(VirtualTable table)
    {
        var liveTable = Model?.Tables.FirstOrDefault(item => string.Equals(item.TableName, table.Name, StringComparison.OrdinalIgnoreCase));
        return new ErTable
        {
            TableName = table.Name,
            SchemaName = liveTable?.SchemaName ?? string.Empty,
            X = table.X,
            Y = table.Y,
            Width = liveTable?.Width ?? DefaultTableWidth,
            Height = liveTable?.Height ?? DefaultTableHeight,
            ColorGroup = ResolveTableColorGroup(table.Name),
            ColorHex = ResolveTableColor(table.Name),
            Columns = table.Columns.Select(column => new ErColumn
            {
                Name = column.Name,
                DataType = column.DataType,
                IsPrimaryKey = column.IsPrimaryKey,
                IsForeignKey = VirtualSchema.Relationships.Any(relationship => string.Equals(relationship.FromTable, table.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(relationship.FromColumn, column.Name, StringComparison.OrdinalIgnoreCase)),
                IsNullable = column.IsNullable
            }).ToList()
        };
    }

    private VirtualTable? FindVirtualTable(string tableName) =>
        VirtualSchema.Tables.FirstOrDefault(table => string.Equals(table.Name, tableName, StringComparison.OrdinalIgnoreCase));

    private string MakeUniqueTableName(string baseName)
    {
        var normalizedBaseName = string.IsNullOrWhiteSpace(baseName) ? "new_table" : baseName.Trim();
        var candidate = normalizedBaseName;
        var index = 1;
        while (NameExists(candidate))
            candidate = $"{normalizedBaseName}_{index++}";
        return candidate;
    }

    private string EnsureUniqueTableName(string proposedName, VirtualTable current)
    {
        var candidate = string.IsNullOrWhiteSpace(proposedName) ? current.Name : proposedName.Trim();
        var baseName = candidate;
        var index = 1;
        while (TableNameExists(candidate, current))
            candidate = $"{baseName}_{index++}";
        return candidate;
    }

    private bool NameExists(string tableName) =>
        VirtualSchema.Tables.Any(table => string.Equals(table.Name, tableName, StringComparison.OrdinalIgnoreCase))
        || Model?.Tables.Any(table => string.Equals(table.TableName, tableName, StringComparison.OrdinalIgnoreCase)) == true;

    private bool TableNameExists(string tableName, VirtualTable current) =>
        VirtualSchema.Tables.Any(table => !ReferenceEquals(table, current) && string.Equals(table.Name, tableName, StringComparison.OrdinalIgnoreCase));

    private string MakeUniqueColumnName(string baseName)
    {
        var normalizedBaseName = string.IsNullOrWhiteSpace(baseName) ? "column" : baseName.Trim();
        var candidate = normalizedBaseName;
        var index = 1;
        while (EditingColumns.Any(column => string.Equals(column.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            candidate = $"{normalizedBaseName}_{index++}";
        return candidate;
    }

    private void ClearRelationshipDraft()
    {
        _relationshipStartTable = null;
        _relationshipStartColumn = null;
    }

}
