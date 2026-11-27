using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;

namespace DbConnect.ViewModels;

public partial class SnippetEditorDialogViewModel : ViewModelBase
{
    private readonly Snippet _original;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _body = string.Empty;

    [ObservableProperty]
    private string _tagsText = string.Empty;

    [ObservableProperty]
    private SnippetScope _scope = SnippetScope.Global;

    [ObservableProperty]
    private string _connectionName = string.Empty;

    [ObservableProperty]
    private string _databaseName = string.Empty;

    public SnippetScope[] AvailableScopes { get; } = Enum.GetValues<SnippetScope>();

    public Snippet? Result { get; private set; }

    public SnippetEditorDialogViewModel(Snippet? existing = null)
    {
        _original = existing ?? new Snippet();
        if (existing != null)
        {
            Name = existing.Name;
            Description = existing.Description;
            Body = existing.Body;
            TagsText = string.Join(", ", existing.Tags);
            Scope = existing.Scope;
            ConnectionName = existing.ConnectionName ?? string.Empty;
            DatabaseName = existing.DatabaseName ?? string.Empty;
        }
    }

    [RelayCommand]
    private void Save()
    {
        Result = new Snippet
        {
            Id = _original.Id,
            Name = Name,
            Description = Description,
            Body = Body,
            Tags = TagsText
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList(),
            Scope = Scope,
            ConnectionName = string.IsNullOrWhiteSpace(ConnectionName) ? null : ConnectionName,
            DatabaseName = string.IsNullOrWhiteSpace(DatabaseName) ? null : DatabaseName,
            CreatedAt = _original.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
            LastUsedAt = _original.LastUsedAt
        };
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
    }
}
