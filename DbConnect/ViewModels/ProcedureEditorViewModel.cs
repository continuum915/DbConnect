using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FluentIcons.Common;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class ProcedureEditorViewModel : ViewModelBase
{
    private readonly DatabaseSession _session;
    private readonly string _schemaName;
    private readonly string _originalRoutineName;
    private readonly bool _isNewRoutine;
    private bool _isDirty;
    private bool _suppressDirtyTracking;
    private SchemaNodeType _routineType;

    [ObservableProperty] private string _definition = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private QueryResult? _executionResult;
    [ObservableProperty] private bool _showParameters;

    public ProcedureEditorViewModel(
        DatabaseSession session,
        SchemaNodeType routineType,
        string displayName,
        string routineName,
        string schemaName = "",
        bool isNewRoutine = false)
    {
        _session = session;
        _routineType = routineType;
        _schemaName = schemaName;
        _originalRoutineName = routineName;
        _isNewRoutine = isNewRoutine;
        Title = displayName;
    }

    public DatabaseSession Session => _session;
    public ObservableCollection<ProcedureParameter> Parameters { get; } = [];
    public Func<Task>? OnRequestRefresh { get; set; }
    public Func<ProcedureRunnerViewModel, Task>? ShowRunnerFunc { get; set; }
    public override bool IsDirty => _isDirty;
    public override Symbol? TabIcon => Symbol.Code;
    public override string? LabelColor => Session.Connection.LabelColor;

    partial void OnDefinitionChanged(string value)
    {
        if (_suppressDirtyTracking)
            return;

        _isDirty = true;
        OnPropertyChanged(nameof(IsDirty));
        RefreshParameters();
    }

    public async Task LoadAsync()
    {
        if (_isNewRoutine)
        {
            SetDefinition(_session.GetNewProcedureTemplate(GetDefaultRoutineName()));
            StatusMessage = "New routine template ready.";
            HasError = false;
            return;
        }

        IsLoading = true;
        StatusMessage = null;
        HasError = false;

        try
        {
            string? definition = _routineType switch
            {
                SchemaNodeType.Function => await _session.GetFunctionDefinitionAsync(_schemaName, _originalRoutineName),
                SchemaNodeType.Trigger => await _session.GetTriggerDefinitionAsync(_schemaName, _originalRoutineName),
                _ => await _session.GetProcedureDefinitionAsync(_schemaName, _originalRoutineName)
            };

            SetDefinition(definition ?? _session.GetNewProcedureTemplate(GetDefaultRoutineName()));
            if (definition == null)
                StatusMessage = "Definition not available; loaded a template instead.";
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        HasError = false;
        StatusMessage = null;

        try
        {
            await _session.SaveProcedureAsync(Definition, affectedScope: SchemaChangeScopes.Routine(_session, _schemaName, _routineType));
            _isDirty = false;
            OnPropertyChanged(nameof(IsDirty));
            StatusMessage = "Saved successfully.";
            if (OnRequestRefresh != null)
                await OnRequestRefresh();
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private async Task ExecuteAsync()
    {
        HasError = false;
        StatusMessage = null;

        try
        {
            var sql = BuildExecutionStatement();
            ExecutionResult = await _session.ExecuteQueryAsync(sql);
            HasError = ExecutionResult.HasError;
            StatusMessage = ExecutionResult.HasError
                ? ExecutionResult.ErrorMessage
                : ExecutionResult.HasResultSet
                    ? $"Execution returned {ExecutionResult.ObjectRows.Count} row(s)."
                    : ExecutionResult.RecordsAffected >= 0
                        ? $"Execution completed. {ExecutionResult.RecordsAffected} row(s) affected."
                        : "Execution completed.";
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (ShowRunnerFunc == null)
            return;

        var routineType = RoutineDefinitionParser.ExtractRoutineType(Definition) ?? _routineType;
        var qualifiedRoutineName = RoutineDefinitionParser.ExtractQualifiedRoutineName(Definition)
            ?? GetQualifiedRoutineName();
        var vm = new ProcedureRunnerViewModel(
            _session,
            _schemaName,
            _originalRoutineName,
            routineType,
            qualifiedRoutineName,
            Parameters.Select(CloneParameter).ToList());
        await ShowRunnerFunc(vm);
    }

    private static ProcedureParameter CloneParameter(ProcedureParameter parameter) => new()
    {
        Name = parameter.Name,
        DataType = parameter.DataType,
        Direction = parameter.Direction,
        DefaultValue = parameter.DefaultValue,
        RuntimeValue = parameter.RuntimeValue,
        OrdinalPosition = parameter.OrdinalPosition
    };

    private void RefreshParameters()
    {
        var runtimeValues = Parameters.ToDictionary(p => p.Name, p => p.RuntimeValue, StringComparer.OrdinalIgnoreCase);
        var parsed = _session.ParseParameters(Definition);

        Parameters.Clear();
        foreach (var parameter in parsed)
        {
            if (runtimeValues.TryGetValue(parameter.Name, out var runtimeValue))
                parameter.RuntimeValue = runtimeValue;
            Parameters.Add(parameter);
        }

        ShowParameters = Parameters.Any(parameter => parameter.Direction != ParameterDirection.Out);
    }

    private void SetDefinition(string value)
    {
        _suppressDirtyTracking = true;
        Definition = value;
        _suppressDirtyTracking = false;
        _isDirty = false;
        OnPropertyChanged(nameof(IsDirty));
        RefreshParameters();
    }

    private string BuildExecutionStatement()
    {
        var routineName = RoutineDefinitionParser.ExtractQualifiedRoutineName(Definition)
            ?? GetQualifiedRoutineName();
        var routineType = RoutineDefinitionParser.ExtractRoutineType(Definition) ?? _routineType;
        var arguments = string.Join(", ", Parameters
            .Where(parameter => parameter.Direction != ParameterDirection.Out)
            .Select(parameter => ToSqlLiteral(parameter.RuntimeValue, parameter.DefaultValue)));

        return routineType == SchemaNodeType.Function
            ? $"SELECT {routineName}({arguments});"
            : $"CALL {routineName}({arguments});";
    }

    private string GetQualifiedRoutineName() => string.IsNullOrWhiteSpace(_schemaName)
        ? _originalRoutineName
        : $"{_schemaName}.{_originalRoutineName}";

    private string GetDefaultRoutineName() => string.IsNullOrWhiteSpace(_schemaName)
        ? _originalRoutineName
        : $"{_schemaName}.{_originalRoutineName}";

    private static string ToSqlLiteral(string? runtimeValue, string? defaultValue)
    {
        var value = string.IsNullOrWhiteSpace(runtimeValue) ? defaultValue : runtimeValue;
        if (string.IsNullOrWhiteSpace(value))
            return "NULL";

        var trimmed = value.Trim();
        if (trimmed.StartsWith("'", StringComparison.Ordinal)
            || trimmed.StartsWith("\"", StringComparison.Ordinal)
            || decimal.TryParse(trimmed, NumberStyles.Any, CultureInfo.InvariantCulture, out _)
            || trimmed.Equals("NULL", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("FALSE", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("@", StringComparison.Ordinal)
            || trimmed.Contains('('))
        {
            return trimmed;
        }

        return $"'{trimmed.Replace("'", "''", StringComparison.Ordinal)}'";
    }
}
