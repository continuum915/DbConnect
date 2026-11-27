using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class ProcedureRunnerViewModel : ObservableObject
{
    private readonly DatabaseSession _session;
    private readonly string _schemaName;
    private readonly string _routineName;
    private readonly string _qualifiedRoutineName;
    private readonly SchemaNodeType _routineType;
    private readonly IReadOnlyList<ProcedureParameter> _detectedParameters;
    private List<ProcedureParameter> _routineParameters = [];

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _executionTime = "00:00:00";
    [ObservableProperty] private string _statusMessage = "Ready";

    public ObservableCollection<ParameterInputRow> Parameters { get; } = new();
    public ObservableCollection<QueryResult> ResultSets { get; } = new();
    public ObservableCollection<string> Messages { get; } = new();
    public ObservableCollection<ParameterOutputRow> OutputParameters { get; } = new();

    public ProcedureRunnerViewModel(
        DatabaseSession session,
        string schemaName,
        string routineName,
        SchemaNodeType routineType,
        string qualifiedRoutineName,
        IReadOnlyList<ProcedureParameter>? detectedParameters = null)
    {
        _session = session;
        _schemaName = schemaName;
        _routineName = routineName;
        _routineType = routineType;
        _qualifiedRoutineName = qualifiedRoutineName;
        _detectedParameters = detectedParameters ?? [];
    }

    public async Task InitializeAsync()
    {
        IsBusy = true;
        StatusMessage = "Loading parameters…";

        try
        {
            var providerParameters = await LoadProviderParametersAsync();
            _routineParameters = MergeParameters(providerParameters);

            Parameters.Clear();
            foreach (var parameter in _routineParameters
                         .Where(parameter => parameter.Direction != ParameterDirection.Out)
                         .OrderBy(parameter => parameter.OrdinalPosition))
            {
                Parameters.Add(new ParameterInputRow(parameter));
            }

            StatusMessage = Parameters.Count > 0
                ? $"Loaded {Parameters.Count} input parameter(s)."
                : "No input parameters detected.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExecuteAsync(CancellationToken ct)
    {
        IsBusy = true;
        StatusMessage = "Executing…";
        ResultSets.Clear();
        Messages.Clear();
        OutputParameters.Clear();
        var sw = Stopwatch.StartNew();

        try
        {
            if (_routineParameters.Count == 0)
                _routineParameters = MergeParameters(await LoadProviderParametersAsync());

            var inputRows = Parameters.ToDictionary(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase);
            var inputValues = _routineParameters
                .OrderBy(parameter => parameter.OrdinalPosition)
                .Select(parameter => new DbParameterValue(
                    parameter.Name,
                    ConvertToTypedValue(
                        inputRows.TryGetValue(parameter.Name, out var inputRow)
                            ? inputRow.Value
                            : parameter.RuntimeValue ?? parameter.DefaultValue,
                        parameter.DataType),
                    parameter.DataType,
                    parameter.Direction))
                .ToList();

            var result = await _session.ExecuteRoutineAsync(
                _schemaName,
                _routineName,
                _qualifiedRoutineName,
                _routineType,
                inputValues,
                ct);

            foreach (var rs in result.ResultSets)
                ResultSets.Add(rs);
            foreach (var msg in result.Messages)
                Messages.Add(msg);
            foreach (var op in result.OutputParameters)
                OutputParameters.Add(new ParameterOutputRow(op.Name, op.Value));

            StatusMessage = "Execution completed successfully.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Execution cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Execution failed: {ex.Message}";
            Messages.Add($"[Error] {ex.Message}");
        }
        finally
        {
            sw.Stop();
            ExecutionTime = sw.Elapsed.ToString(@"hh\:mm\:ss\.fff");
            IsBusy = false;
        }
    }

    private async Task<IReadOnlyList<ProcedureParameter>> LoadProviderParametersAsync()
    {
        try
        {
            return await _session.GetProcedureParametersAsync(_schemaName, _routineName) ?? [];
        }
        catch (Exception ex)
        {
            if (Messages.Count == 0)
                Messages.Add($"[Warning] Falling back to definition parameters: {ex.Message}");
            return [];
        }
    }

    private List<ProcedureParameter> MergeParameters(IReadOnlyList<ProcedureParameter> providerParameters)
    {
        if (_detectedParameters.Count == 0)
            return providerParameters.Select(CloneParameter).OrderBy(parameter => parameter.OrdinalPosition).ToList();

        var providerByName = providerParameters
            .GroupBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => new Queue<ProcedureParameter>(group.OrderBy(parameter => parameter.OrdinalPosition)), StringComparer.OrdinalIgnoreCase);
        var merged = new List<ProcedureParameter>();

        for (var i = 0; i < _detectedParameters.Count; i++)
        {
            var detected = _detectedParameters[i];
            ProcedureParameter? providerParameter = null;
            if (!string.IsNullOrWhiteSpace(detected.Name)
                && providerByName.TryGetValue(detected.Name, out var bucket)
                && bucket.Count > 0)
            {
                providerParameter = bucket.Dequeue();
            }
            else if (i < providerParameters.Count)
            {
                providerParameter = providerParameters[i];
            }

            merged.Add(new ProcedureParameter
            {
                Name = string.IsNullOrWhiteSpace(detected.Name) ? providerParameter?.Name ?? $"arg{i + 1}" : detected.Name,
                DataType = string.IsNullOrWhiteSpace(detected.DataType) ? providerParameter?.DataType ?? string.Empty : detected.DataType,
                Direction = providerParameter != null && detected.Direction == ParameterDirection.In && providerParameter.Direction != ParameterDirection.In
                    ? providerParameter.Direction
                    : detected.Direction,
                DefaultValue = detected.DefaultValue,
                RuntimeValue = detected.RuntimeValue ?? detected.DefaultValue,
                OrdinalPosition = detected.OrdinalPosition > 0 ? detected.OrdinalPosition : providerParameter?.OrdinalPosition ?? i + 1
            });
        }

        foreach (var providerParameter in providerParameters)
        {
            if (merged.Any(parameter => parameter.OrdinalPosition == providerParameter.OrdinalPosition
                && string.Equals(parameter.Name, providerParameter.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            merged.Add(CloneParameter(providerParameter));
        }

        return merged.OrderBy(parameter => parameter.OrdinalPosition).ToList();
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

    private static object? ConvertToTypedValue(object? rawValue, string dataType)
    {
        if (rawValue is null)
            return null;

        if (string.IsNullOrWhiteSpace(dataType))
            return rawValue;

        if (rawValue is not string text)
            return rawValue;

        var trimmed = text.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            return null;

        if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
            trimmed = trimmed[1..^1].Replace("''", "'", StringComparison.Ordinal);

        var normalizedType = dataType.Trim().ToUpperInvariant();
        if (normalizedType.Contains("BOOL", StringComparison.Ordinal) || normalizedType == "BIT")
        {
            if (trimmed is "1" or "0")
                return trimmed == "1";
            if (bool.TryParse(trimmed, out var boolValue))
                return boolValue;
        }

        if (normalizedType.Contains("BIGINT", StringComparison.Ordinal) && long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
            return longValue;

        if ((normalizedType.Contains("INT", StringComparison.Ordinal) || normalizedType.Contains("SERIAL", StringComparison.Ordinal))
            && int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
            return intValue;

        if ((normalizedType.Contains("DECIMAL", StringComparison.Ordinal)
             || normalizedType.Contains("NUMERIC", StringComparison.Ordinal)
             || normalizedType.Contains("MONEY", StringComparison.Ordinal))
            && decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out var decimalValue))
            return decimalValue;

        if ((normalizedType.Contains("FLOAT", StringComparison.Ordinal)
             || normalizedType.Contains("DOUBLE", StringComparison.Ordinal)
             || normalizedType.Contains("REAL", StringComparison.Ordinal))
            && double.TryParse(trimmed, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var doubleValue))
            return doubleValue;

        if ((normalizedType.Contains("UUID", StringComparison.Ordinal) || normalizedType.Contains("UNIQUEIDENTIFIER", StringComparison.Ordinal))
            && Guid.TryParse(trimmed, out var guidValue))
            return guidValue;

        return trimmed;
    }
}

public class ParameterInputRow : ObservableObject
{
    public string Name { get; }
    public string DataType { get; }
    public ParameterDirection Direction { get; }
    public string DirectionLabel => Direction.ToString().ToUpperInvariant();

    private object? _value;
    public object? Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public ParameterInputRow(ProcedureParameter p)
    {
        Name = p.Name;
        DataType = p.DataType;
        Direction = p.Direction;
        Value = p.RuntimeValue ?? p.DefaultValue;
    }
}

public record ParameterOutputRow(string Name, object? Value);
