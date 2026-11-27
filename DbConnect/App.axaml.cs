using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Markup.Xaml;
using DbConnect.ViewModels;
using DbConnect.Views;
using DbConnect.Models;
using DbConnect.Services;

namespace DbConnect;

public partial class App : Application
{
    private readonly DiagnosticsService _diagnostics = DiagnosticsService.Default;

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        DbConnect.Styles.CodeFontResource.Attach(this);
        WindowChromeTheming.Register();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit. 
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            DisableAvaloniaDataAnnotationValidation();

            var dbService = new DbConnect.Services.DatabaseService();
            var queryWorkspace = new DbConnect.Services.QueryWorkspaceService();
            var settingsService = new DbConnect.Services.SettingsService();
            var mainVm = new MainWindowViewModel(dbService, queryWorkspace, settingsService);
            var mainWindow = new MainWindow { DataContext = mainVm };
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs args)
    {
        var operation = _diagnostics.BeginOperation("application", "unhandled-exception");
        if (args.ExceptionObject is Exception exception)
            _diagnostics.RecordFailure(operation, exception, "Unhandled application exception.");
        else
            _diagnostics.Record(operation, DiagnosticSeverity.Critical, "Unhandled application exception.");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        var operation = _diagnostics.BeginOperation("application", "unobserved-task-exception");
        _diagnostics.RecordFailure(operation, args.Exception, "Unobserved background task exception.");
        args.SetObserved();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}
