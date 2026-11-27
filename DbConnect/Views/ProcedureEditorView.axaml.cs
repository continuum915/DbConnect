using System;
using Avalonia.Controls;
using Avalonia.Input;
using AvaloniaEdit;
using AvaloniaEdit.Search;
using DbConnect.ViewModels;

namespace DbConnect.Views;

public partial class ProcedureEditorView : UserControl
{
    private readonly TextEditor _editor;
    private readonly SearchPanel? _searchPanel;
    private ProcedureEditorViewModel? _boundViewModel;
    private bool _isUpdatingEditor;

    public ProcedureEditorView()
    {
        InitializeComponent();
        _editor = this.FindControl<TextEditor>("DefinitionEditor")!;
        _editor.TextChanged += Editor_TextChanged;
        _editor.KeyDown += Editor_KeyDown;
        _searchPanel = SearchPanel.Install(_editor);
        EditorThemeBinding.TryAttach(_editor, ".sql");
        EditorPreferencesBinding.Attach(_editor);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_boundViewModel != null)
            _boundViewModel.PropertyChanged -= ViewModel_PropertyChanged;

        if (DataContext is not ProcedureEditorViewModel vm)
        {
            _boundViewModel = null;
            return;
        }

        _boundViewModel = vm;
        _boundViewModel.PropertyChanged += ViewModel_PropertyChanged;
        SetEditorText(vm.Definition);
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProcedureEditorViewModel.Definition) && DataContext is ProcedureEditorViewModel vm)
            SetEditorText(vm.Definition);
    }

    private void SetEditorText(string text)
    {
        if (_editor.Text == text)
            return;

        _isUpdatingEditor = true;
        _editor.Text = text;
        _isUpdatingEditor = false;
    }

    private void Editor_TextChanged(object? sender, EventArgs e)
    {
        if (_isUpdatingEditor)
            return;

        if (DataContext is ProcedureEditorViewModel vm)
            vm.Definition = _editor.Text;
    }

    private void Editor_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ProcedureEditorViewModel vm)
            return;

        if (e.Key == Key.F5)
        {
            vm.ExecuteCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control)
        {
            vm.SaveCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            _searchPanel?.Open();
            e.Handled = true;
        }
    }
}
