using CommunityToolkit.Mvvm.ComponentModel;
using DbConnect.Models;

namespace DbConnect.ViewModels;

public partial class ProductionConfirmViewModel : ViewModelBase
{
    public DestructiveActionPreview Preview { get; }
    public string ObjectName { get; }
    public string PromptText { get; }
    public string WarningText => Preview.EnvironmentTier.Equals("Production", System.StringComparison.OrdinalIgnoreCase)
        ? "⚠️ WARNING: This is a PRODUCTION environment!"
        : "Review this destructive action before continuing.";
    public string TargetText => $"Target: {Preview.TargetConnection} [{Preview.EnvironmentTier}]";
    public string ActionText => $"Action: {Preview.Action} {Preview.ObjectType} {Preview.ObjectName}";
    public string SqlPreview => Preview.Sql;
    public bool Confirmed { get; private set; }

    [ObservableProperty]
    private string _typedName = string.Empty;

    public bool CanConfirm => TypedName == ObjectName;

    public ProductionConfirmViewModel(string objectName)
        : this(new DestructiveActionPreview("Destructive action", "object", objectName, "Unknown target", "Production", string.Empty))
    {
    }

    public ProductionConfirmViewModel(DestructiveActionPreview preview)
    {
        Preview = preview;
        ObjectName = preview.ObjectName;
        PromptText = $"Type \"{preview.ObjectName}\" to confirm:";
    }

    partial void OnTypedNameChanged(string value)
    {
        OnPropertyChanged(nameof(CanConfirm));
    }

    public void Confirm()
    {
        if (TypedName == ObjectName)
            Confirmed = true;
    }
}
