using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbConnect.Models;
using DbConnect.Services;
using DbConnect.Services.Builders;

namespace DbConnect.ViewModels;

public partial class ViewDesignerViewModel : ViewModelBase
{
    private readonly DatabaseSession _session = null!;
    public DatabaseSession? Session => _session;
    private readonly SchemaNode? _viewNode;
    private bool _isExistingView;

    [ObservableProperty] private string _viewName = "new_view";
    [ObservableProperty] private string _selectSql = "SELECT * FROM ...";
    [ObservableProperty] private QueryResult? _previewResult;
    [ObservableProperty] private bool _isPreviewing;
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string _previewSql = "";
    public Func<Task>? OnRequestRefresh { get; set; }
    public bool SupportsCreateOrReplaceView => _session?.Capabilities.SupportsCreateOrReplaceView == true;
    public string ViewOperationHint => SupportsCreateOrReplaceView
        ? "This provider supports CREATE OR REPLACE VIEW for replacement workflows."
        : "Replacing an existing view on this provider requires a drop/create workflow.";

    public ViewDesignerViewModel(DatabaseSession session)
    {
        _session = session;
        Title = "New View";
    }

    public ViewDesignerViewModel(SchemaNode viewNode, DatabaseSession session) : this(session)
    {
        _viewNode = viewNode;
        _isExistingView = true;
        ViewName = viewNode.RawName ?? viewNode.Name;
        Title = $"Edit View: {viewNode.Name}";
        _ = LoadExistingViewAsync();
    }

    public ViewDesignerViewModel() { }

    public ViewDesignerViewModel(IDatabaseProvider provider, string connectionString)
        : this(new DatabaseSession(
            new ConnectionInfo
            {
                Name = "View Designer",
                Provider = provider.ProviderName,
                ConnectionString = connectionString
            },
            provider))
    {
    }

    private async Task LoadExistingViewAsync()
    {
        IsLoading = true;
        try
        {
            var definition = await _session.GetViewDefinitionAsync(ViewName);
            if (definition != null)
            {
                SelectSql = ExtractSelectBody(definition, ViewName);
            }
            UpdateViewPreview();
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Error loading view: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string ExtractSelectBody(string definition, string viewName)
    {
        // Match the AS keyword that follows the view name in: CREATE [OR REPLACE] [TEMP] VIEW [schema.]name AS ...
        // Using a targeted pattern avoids splitting on AS inside column aliases or quoted identifiers.
        var match = Regex.Match(definition,
            @"(?i)CREATE\s+(?:OR\s+REPLACE\s+)?(?:TEMP(?:ORARY)?\s+)?VIEW\s+(?:[\w\""`.]+\s*\.\s*)?[\w\""`.]+\s+AS\s+");
        if (match.Success)
        {
            var body = definition.Substring(match.Index + match.Length).Trim();
            if (body.EndsWith(";")) body = body[..^1].TrimEnd();
            return body;
        }
        return definition;
    }

    public void UpdateViewPreview()
    {
        if (_session == null) return;
        PreviewSql = DdlBuilderFactory.For(_session.ProviderName)
            .GenerateCreateView(ViewName, SelectSql, _session.Capabilities.SupportsCreateOrReplaceView);
    }

    partial void OnViewNameChanged(string value) => UpdateViewPreview();
    partial void OnSelectSqlChanged(string value) => UpdateViewPreview();

    [RelayCommand]
    private async Task RunPreviewAsync()
    {
        IsPreviewing = true;
        StatusMessage = "Fetching preview...";
        try
        {
            var query = _session.FormatLimitQuery(SelectSql, 100);
            PreviewResult = await _session.ExecuteQueryAsync(query);
            StatusMessage = PreviewResult.HasError ? $"❌ {PreviewResult.ErrorMessage}" : "Preview loaded.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Error: {ex.Message}";
        }
        finally
        {
            IsPreviewing = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        StatusMessage = _isExistingView ? "Saving view..." : "Creating view...";
        try
        {
            var builder = DdlBuilderFactory.For(_session.ProviderName);

            if (_isExistingView && !SupportsCreateOrReplaceView)
            {
                var dropSql = $"DROP VIEW {builder.Quote(ViewName)};";
                await _session.ExecuteNonQueryAsync(dropSql, _viewNode == null ? MetadataScope.Root : SchemaChangeScopes.Membership(_session, _viewNode));
            }

            var createSql = builder.GenerateCreateView(ViewName, SelectSql,
                SupportsCreateOrReplaceView && _isExistingView);
            await _session.ExecuteNonQueryAsync(createSql, _viewNode == null ? MetadataScope.Root : SchemaChangeScopes.Membership(_session, _viewNode));
            StatusMessage = _isExistingView ? "✅ View saved." : "✅ View created successfully.";
            if (OnRequestRefresh != null) await OnRequestRefresh();
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Save failed: {ex.Message}";
        }
        finally
        {
            IsSaving = false;
        }
    }
}
