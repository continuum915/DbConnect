using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;

namespace DbConnect.ViewModels;

public partial class CsvImportWizardViewModel
{
    private CsvImportAssessment? _pendingRisk;
    private string? _riskAcknowledgement;

    [ObservableProperty]
    private bool _hasImportWarning;

    [RelayCommand]
    private async Task ProceedAnywayAsync()
    {
        if (!HasImportWarning || _pendingRisk == null || CurrentStep != WizardStep.ConfigureColumns) return;
        // The service re-assesses provider, target, mode and risks immediately before execution.
        _riskAcknowledgement = _pendingRisk.Token;
        HasImportWarning = false;
        await NextStepAsync();
        _riskAcknowledgement = null;
    }

    [RelayCommand]
    private void CancelImportWarning()
    {
        _pendingRisk = null;
        _riskAcknowledgement = null;
        HasImportWarning = false;
        StatusMessage = "Import cancelled. No writes were performed.";
    }
}
