using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using DbConnect.Models;

namespace DbConnect.ViewModels;

public partial class CsvImportWizardViewModel
{
    private (string Path, int Delimiter, bool Header, ImportMode Mode, string Table, string Hash)? _previewInput;
    private Dictionary<CsvColumnSpec, int?> _sourceOrdinals = new();

    private (string Path, int Delimiter, bool Header, ImportMode Mode, string Table, string Hash) CaptureInput()
    {
        using var stream = File.OpenRead(FilePath);
        return (FilePath, DelimiterIndex, HasHeaderRow, ImportMode, TableName,
            Convert.ToHexString(SHA256.HashData(stream)));
    }

    private void ClearPreview()
    {
        _pendingRisk = null;
        _riskAcknowledgement = null;
        HasImportWarning = false;
        _previewInput = null;
        _sourceOrdinals.Clear();
        ColumnSpecs.Clear();
        PreviewRows.Clear();
        DetectionSummary = string.Empty;
        PreviewDdl = string.Empty;
    }

    private bool ValidateExecution()
    {
        try
        {
            if (_previewInput == null || _previewInput != CaptureInput())
            {
                ClearPreview();
                CurrentStep = WizardStep.PickFile;
                StatusMessage = "Input changed. Reload the preview before importing.";
                return false;
            }
        }
        catch (Exception ex)
        {
            ClearPreview();
            CurrentStep = WizardStep.PickFile;
            StatusMessage = $"Error reading file: {ex.Message}. Reload the preview before importing.";
            return false;
        }

        var included = ColumnSpecs.Where(s => s.Include).ToList();
        if (included.Count == 0)
            StatusMessage = "Select at least one source column.";
        else if (ColumnSpecs.Any(s => !_sourceOrdinals.TryGetValue(s, out var ordinal) || s.SourceOrdinal != ordinal))
            StatusMessage = "Source columns changed. Go back and reload the preview.";
        else if (included.Any(s => string.IsNullOrWhiteSpace(s.TargetColumnName) || s.TargetColumnName.Contains('\0')))
            StatusMessage = "Target column names must be nonblank and contain no NUL characters.";
        else if (included.Select(s => s.TargetColumnName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != included.Count)
            StatusMessage = "Target column names must be unique. Rename or exclude duplicate columns.";
        else if (string.IsNullOrWhiteSpace(TableName) || TableName.Contains('\0'))
            StatusMessage = "Please enter a valid table name.";
        else
            return true;

        return false;
    }
}
