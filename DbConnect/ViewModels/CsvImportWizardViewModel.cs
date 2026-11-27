using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public enum WizardStep { PickFile, ConfigureColumns, Executing, Done }

public partial class CsvImportWizardViewModel : ViewModelBase
{
    private readonly DatabaseSession? _session;
    private TopLevel? _topLevel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStepPickFile))]
    [NotifyPropertyChangedFor(nameof(IsStepConfigureColumns))]
    [NotifyPropertyChangedFor(nameof(IsStepExecuting))]
    [NotifyPropertyChangedFor(nameof(IsStepDone))]
    [NotifyPropertyChangedFor(nameof(IsNotDone))]
    private WizardStep _currentStep = WizardStep.PickFile;

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNewTableMode))]
    [NotifyPropertyChangedFor(nameof(TableName))]
    private ImportMode _importMode = ImportMode.NewTable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TableName))]
    private string _newTableName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TableName))]
    private string _existingTableName = string.Empty;

    [ObservableProperty]
    private int _delimiterIndex = 0;

    [ObservableProperty]
    private bool _hasHeaderRow = true;

    [ObservableProperty]
    private bool _truncateBeforeImport = false;

    [ObservableProperty]
    private string _previewDdl = string.Empty;

    [ObservableProperty]
    private string _detectionSummary = string.Empty;

    [ObservableProperty]
    private int _rowsImported;

    [ObservableProperty]
    private bool _importSucceeded;

    [ObservableProperty]
    private string _importError = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public ObservableCollection<CsvColumnSpec> ColumnSpecs { get; } = new();
    public ObservableCollection<string[]> PreviewRows { get; } = new();

    public bool IsStepPickFile => CurrentStep == WizardStep.PickFile;
    public bool IsStepConfigureColumns => CurrentStep == WizardStep.ConfigureColumns;
    public bool IsStepExecuting => CurrentStep == WizardStep.Executing;
    public bool IsStepDone => CurrentStep == WizardStep.Done;
    public bool IsNotDone => CurrentStep != WizardStep.Done;
    public bool IsNewTableMode => ImportMode == ImportMode.NewTable;

    public string TableName => ImportMode == ImportMode.NewTable ? NewTableName : ExistingTableName;

    public static IReadOnlyList<string> DelimiterOptions { get; } = new[] { "Comma (,)", "Tab (\\t)", "Pipe (|)", "Semicolon (;)" };
    public static IReadOnlyList<ImportMode> ImportModeOptions { get; } = Enum.GetValues<ImportMode>();
    public static IReadOnlyList<DetectedColumnType> ColumnTypeOptions { get; } = Enum.GetValues<DetectedColumnType>();

    public CsvImportWizardViewModel(DatabaseSession session)
    {
        _session = session;
        Title = "Import CSV";
    }

    public CsvImportWizardViewModel()
    {
        Title = "Import CSV";
    }

    public void SetTopLevel(TopLevel? tl) => _topLevel = tl;

    private char GetDelimiterChar() => DelimiterIndex switch
    {
        1 => '\t',
        2 => '|',
        3 => ';',
        _ => ','
    };

    [RelayCommand]
    private async Task BrowseFileAsync()
    {
        if (_topLevel == null) return;

        var files = await _topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select CSV File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("CSV Files") { Patterns = new[] { "*.csv", "*.tsv", "*.txt" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*" } }
            }
        });

        if (files.Count > 0)
        {
            FilePath = files[0].Path.LocalPath;
            if (string.IsNullOrWhiteSpace(NewTableName))
                NewTableName = Path.GetFileNameWithoutExtension(FilePath);
        }
    }

    [RelayCommand]
    private async Task NextStepAsync()
    {
        if (CurrentStep == WizardStep.PickFile)
        {
            ClearPreview();
            if (string.IsNullOrWhiteSpace(FilePath) || !File.Exists(FilePath))
            {
                StatusMessage = "Please select a valid CSV file.";
                return;
            }
            if (string.IsNullOrWhiteSpace(TableName))
            {
                StatusMessage = "Please enter a table name.";
                return;
            }

            await LoadColumnSpecsAsync();
            if (ColumnSpecs.Count > 0)
                CurrentStep = WizardStep.ConfigureColumns;
        }
        else if (CurrentStep == WizardStep.ConfigureColumns)
        {
            if (!ValidateExecution()) return;
            UpdatePreviewDdl();
            CurrentStep = WizardStep.Executing;
            await RunImportAsync();
        }
    }

    [RelayCommand]
    private void PreviousStep()
    {
        if (CurrentStep == WizardStep.ConfigureColumns)
            CurrentStep = WizardStep.PickFile;
    }

    [RelayCommand]
    private void Close()
    {
        // The view handles closing
    }

    private async Task LoadColumnSpecsAsync()
    {
        try
        {
            StatusMessage = string.Empty;
            var input = CaptureInput();
            var preview = await CsvImportService.ParsePreviewAsync(FilePath, GetDelimiterChar(), HasHeaderRow);

            if (preview.TotalDataRows == 0)
            {
                StatusMessage = "The file contains no importable data. Add data records or turn off First row is header for a headerless file.";
                return;
            }

            var specs = CsvImportService.DetectColumnTypes(preview);

            if (ImportMode == ImportMode.ExistingTable && _session != null && !string.IsNullOrWhiteSpace(ExistingTableName))
            {
                var tableCols = await GetTableColumnsAsync();
                foreach (var spec in specs)
                {
                    var match = tableCols.FirstOrDefault(c =>
                        string.Equals(c, spec.CsvHeader, StringComparison.OrdinalIgnoreCase));
                    if (match != null) spec.TargetColumnName = match;
                }
            }

            if (input != CaptureInput())
            {
                StatusMessage = "Input changed while loading. Reload the preview.";
                return;
            }

            foreach (var s in specs) ColumnSpecs.Add(s);
            _previewInput = input;
            _sourceOrdinals = specs.ToDictionary(s => s, s => s.SourceOrdinal);

            PreviewRows.Clear();
            foreach (var r in preview.PreviewRows) PreviewRows.Add(r);

            DetectionSummary = $"Detected {specs.Count} column(s) - {preview.TotalDataRows} data row(s)";
            UpdatePreviewDdl();
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            ClearPreview();
            StatusMessage = $"Error reading file: {ex.Message}";
        }
    }

    private void UpdatePreviewDdl()
    {
        if (ImportMode == ImportMode.NewTable && ColumnSpecs.Count > 0)
        {
            var name = string.IsNullOrWhiteSpace(NewTableName) ? "<table_name>" : NewTableName;
            PreviewDdl = _session == null
                ? CsvImportService.BuildCreateTableSql(name, ColumnSpecs.ToList())
                : CsvImportExecution.CreateTableSql(_session, name, ColumnSpecs.ToList());
        }
        else if (ImportMode == ImportMode.ExistingTable && ColumnSpecs.Count > 0)
        {
            var cols = string.Join(", ", ColumnSpecs.Where(s => s.Include).Select(s => $"\"{s.TargetColumnName}\""));
            PreviewDdl = $"INSERT INTO \"{ExistingTableName}\" ({cols})\nVALUES (...);";
        }
    }

    partial void OnNewTableNameChanged(string value) => UpdatePreviewDdl();

    private async Task<List<string>> GetTableColumnsAsync()
    {
        if (_session == null) return new List<string>();
        try
        {
            var columns = await _session.GetColumnDefinitionsAsync(ExistingTableName);
            return columns.Select(c => c.Name).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private async Task RunImportAsync()
    {
        if (_session == null)
        {
            ImportError = "No database session available.";
            ImportSucceeded = false;
            CurrentStep = WizardStep.Done;
            return;
        }

        StatusMessage = "Importing...";
        var options = new CsvImportOptions
        {
            FilePath = FilePath,
            Delimiter = GetDelimiterChar(),
            HasHeaderRow = HasHeaderRow,
            Mode = ImportMode,
            TableName = TableName,
            TruncateBeforeImport = TruncateBeforeImport,
            RiskAcknowledgement = _riskAcknowledgement,
            ColumnSpecs = ColumnSpecs.ToList(),
            ColumnNames = ColumnSpecs.Where(s => s.Include).Select(s => s.TargetColumnName).ToList()
        };

        var result = await CsvImportService.ExecuteImportAsync(_session, options);
        _riskAcknowledgement = null;
        if (result.RequiredAcknowledgement is { } assessment)
        {
            _pendingRisk = assessment;
            HasImportWarning = true;
            StatusMessage = assessment.Risk.Message;
            CurrentStep = WizardStep.ConfigureColumns;
            return;
        }
        HasImportWarning = false;
        _pendingRisk = null;
        RowsImported = result.RowsImported;
        ImportSucceeded = result.Succeeded;
        ImportError = result.ErrorMessage ?? string.Empty;
        StatusMessage = result.Succeeded
            ? result.RowsImportedIsExact ? $"Successfully imported {result.RowsImported} row(s)." : "Import committed; exact imported-row count is unavailable."
            : $"Import failed: {result.ErrorMessage}";
        CurrentStep = WizardStep.Done;
    }
}
