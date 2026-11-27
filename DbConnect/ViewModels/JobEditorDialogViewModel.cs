using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>
/// Edits the on-demand parts of an automation job. Schedule and enabled state are not edited:
/// jobs run only on demand until scheduling exists (Tier 2 §2.10).
/// </summary>
public partial class JobEditorDialogViewModel : ViewModelBase
{
    public override string Title => "Edit Job";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailLabel))]
    [NotifyPropertyChangedFor(nameof(DetailWatermark))]
    [NotifyPropertyChangedFor(nameof(TypeWarning))]
    [NotifyPropertyChangedFor(nameof(NeedsConnection))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private JobType _type = JobType.RunQuery;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _connectionName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _sqlOrFilePath = string.Empty;

    /// <summary>Job types that can run; an existing job of another type keeps its type in the list.</summary>
    public ObservableCollection<JobType> JobTypes { get; } = new(AutomationService.RunnableJobTypes);

    /// <summary>Names of the currently open connections.</summary>
    public ObservableCollection<string> AvailableConnections { get; } = new();

    public string DetailLabel => Type switch
    {
        JobType.Backup => "Backup file path",
        JobType.DataTransfer => "Transfer profile",
        _ => "SQL to run"
    };

    public string DetailWatermark => Type switch
    {
        JobType.Backup => "Path of the backup file to write",
        JobType.DataTransfer => "Name of a profile saved in Data Transfer",
        _ => "SQL statement(s)"
    };

    /// <summary>A Data Transfer job takes its connections from its profile.</summary>
    public bool NeedsConnection => Type != JobType.DataTransfer;

    public string? TypeWarning => JobTypes.Count > 0 && !AutomationService.RunnableJobTypes.Contains(Type)
        ? $"{Type} jobs cannot run yet. Running this job will fail."
        : null;

    public bool CanSave =>
        !string.IsNullOrWhiteSpace(Name)
        && (!NeedsConnection || !string.IsNullOrWhiteSpace(ConnectionName))
        && !string.IsNullOrWhiteSpace(SqlOrFilePath);

    public void LoadFromJob(AutomationJob job)
    {
        if (!JobTypes.Contains(job.Type))
            JobTypes.Add(job.Type);
        Name = job.Name;
        Type = job.Type;
        ConnectionName = job.ConnectionName;
        SqlOrFilePath = job.SqlOrFilePath;
    }

    public void ApplyTo(AutomationJob job)
    {
        job.Name = Name.Trim();
        job.Type = Type;
        job.ConnectionName = ConnectionName.Trim();
        job.SqlOrFilePath = SqlOrFilePath;
    }
}
