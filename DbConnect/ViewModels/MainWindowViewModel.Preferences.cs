using System.Threading.Tasks;

namespace DbConnect.ViewModels;

public partial class MainWindowViewModel
{
    private PreferencesLauncher? _preferences;

    /// <summary>Confirms SQL that changes data on sensitive connections before it runs.</summary>
    public ExecutionSafety Safety { get; } = new();

    /// <summary>Tools ▸ Preferences.</summary>
    public PreferencesLauncher Preferences => _preferences ??= new PreferencesLauncher(
        () => new PreferencesViewModel(_workspacePersistence.Settings, SelectedTheme, _queryWorkspaceService,
            theme => SelectedTheme = theme,
            settings => _settingsService?.SaveAsync(settings) ?? Task.CompletedTask),
        dialog => ShowDialogFunc?.Invoke(dialog) ?? Task.CompletedTask);
}
