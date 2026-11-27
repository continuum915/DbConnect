using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect.ViewModels;

public partial class SchemaHistoryViewModel : ViewModelBase
{
    private readonly SchemaSnapshotService _svc = new();

    [ObservableProperty] private DatabaseSession? _session;
    [ObservableProperty] private SchemaSnapshot? _snapshotA;
    [ObservableProperty] private SchemaSnapshot? _snapshotB;
    [ObservableProperty] private string _snapshotLabel = string.Empty;
    [ObservableProperty] private bool _isTaking;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public ObservableCollection<SchemaSnapshot> Snapshots { get; } = new();

    public Action<SchemaDiffViewModel>? RequestOpenDiffTab { get; set; }

    public SchemaHistoryViewModel()
    {
        Title = "Schema History";
    }

    partial void OnSessionChanged(DatabaseSession? value)
    {
        if (value != null)
            _ = LoadSnapshotsAsync();
    }

    private async Task LoadSnapshotsAsync()
    {
        if (Session == null) return;
        var list = await _svc.ListSnapshotsAsync(Session.Name);
        Snapshots.Clear();
        foreach (var s in list) Snapshots.Add(s);
    }

    [RelayCommand]
    private async Task TakeSnapshot()
    {
        if (Session == null) { StatusMessage = "No session selected."; return; }
        IsTaking = true;
        StatusMessage = "Taking snapshot...";
        try
        {
            var label = string.IsNullOrWhiteSpace(SnapshotLabel)
                ? $"Snapshot {DateTimeOffset.UtcNow:yyyy-MM-dd HHmmss}"
                : SnapshotLabel.Trim();
            var snap = await _svc.TakeSnapshotAsync(Session, label);
            Snapshots.Insert(0, snap);
            StatusMessage = $"Snapshot '{snap.Label}' taken. {snap.ObjectCount} objects.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally { IsTaking = false; }
    }

    [RelayCommand]
    private async Task DeleteSnapshot(SchemaSnapshot? snapshot)
    {
        if (snapshot == null || Session == null) return;
        await _svc.DeleteSnapshotAsync(snapshot.Id, Session.Name);
        Snapshots.Remove(snapshot);
        StatusMessage = $"Deleted snapshot '{snapshot.Label}'.";
    }

    [RelayCommand]
    private void CompareSnapshots()
    {
        if (SnapshotA == null || SnapshotB == null)
        {
            StatusMessage = "Select both snapshot A and snapshot B.";
            return;
        }
        try
        {
            var nodesA = JsonSerializer.Deserialize<List<SchemaNode>>(SnapshotA.SchemaJson) ?? [];
            var nodesB = JsonSerializer.Deserialize<List<SchemaNode>>(SnapshotB.SchemaJson) ?? [];

            var diffResult = SchemaDiffService.CompareFromNodes(
                SnapshotA.ConnectionName, SnapshotB.ConnectionName, nodesA, nodesB);

            var vm = new SchemaDiffViewModel();
            vm.Title = $"Diff: {SnapshotA.Label} vs {SnapshotB.Label}";
            foreach (var item in diffResult.Items) vm.DiffItems.Add(item);
            vm.CompareStatus = $"Compared {diffResult.Items.Count} objects.";
            RequestOpenDiffTab?.Invoke(vm);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Compare error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportSnapshot(SchemaSnapshot? snapshot)
    {
        if (snapshot == null) return;
        try
        {
            var dir = System.IO.Path.Combine(AppDataPaths.RootDirectory, "exports");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, $"{snapshot.Id}.json");
            await _svc.ExportSnapshotAsync(snapshot, path);
            StatusMessage = $"Exported to {path}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ImportSnapshot()
    {
        StatusMessage = "Import: provide file path via ImportSnapshotFromPath.";
        await Task.CompletedTask;
    }

    public async Task ImportSnapshotFromPath(string filePath)
    {
        try
        {
            var snap = await _svc.ImportSnapshotAsync(filePath);
            Snapshots.Insert(0, snap);
            StatusMessage = $"Imported snapshot '{snap.Label}'.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Import error: {ex.Message}";
        }
    }
}
