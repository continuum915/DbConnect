using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class DependencyAnalysisViewModel : ViewModelBase
{
    private readonly DependencyAnalysisService _service = new();
    private DatabaseSession? _session;

    [ObservableProperty] private string _objectName = string.Empty;
    [ObservableProperty] private string _objectType = string.Empty;
    [ObservableProperty] private string _analysisStatus = string.Empty;

    public ObservableCollection<ObjectDependency> DependsOn { get; } = new();
    public ObservableCollection<ObjectDependency> DependedOnBy { get; } = new();

    public DependencyAnalysisViewModel()
    {
        Title = "Dependencies";
    }

    public async Task LoadAsync(DatabaseSession session, string objectName, string objectType)
    {
        _session = session;
        ObjectName = objectName;
        ObjectType = objectType;
        Title = $"Dependencies: {objectName}";
        AnalysisStatus = "Loading...";
        DependsOn.Clear();
        DependedOnBy.Clear();

        var dependsOn = await _service.GetDependsOnAsync(session, objectName, objectType);
        var dependedOnBy = await _service.GetDependenciesAsync(session, objectName, objectType);

        foreach (var d in dependsOn) DependsOn.Add(d);
        foreach (var d in dependedOnBy) DependedOnBy.Add(d);

        var total = dependsOn.Count + dependedOnBy.Count;
        AnalysisStatus = total == 0
            ? "No dependencies found."
            : $"Found {total} dependencies.";
    }
}
