using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit;
using DbConnect.ViewModels;

namespace DbConnect.Views;

/// <summary>Adapts the find/replace session to the query editor's selection, focus and shortcuts.</summary>
public partial class FindReplaceBar : UserControl
{
    private TextEditor? _editor;
    private bool _selectingMatch;

    public FindReplaceBar()
    {
        InitializeComponent();
        KeyDown += OnBarKeyDown;
    }

    public FindReplaceViewModel? Session => DataContext as FindReplaceViewModel;

    public void Attach(TextEditor editor)
    {
        _editor = editor;
        var session = new FindReplaceViewModel(editor.Document) { IsReadOnly = editor.IsReadOnly };
        DataContext = session;
        session.MatchSelected += (start, length) =>
        {
            _selectingMatch = true;
            try
            {
                editor.Select(start, length);
                editor.ScrollToLine(editor.Document.GetLineByOffset(start).LineNumber);
            }
            finally
            {
                _selectingMatch = false;
            }
        };
        session.Closed += () => editor.TextArea.Focus();
        editor.TextArea.SelectionChanged += (_, _) =>
        {
            if (!_selectingMatch)
                session.MoveCaret(editor.CaretOffset);
        };
        editor.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextEditor.IsReadOnlyProperty)
                session.IsReadOnly = editor.IsReadOnly;
        };
        editor.TemplateApplied += (_, _) => editor.SearchPanel?.Uninstall();
        editor.SearchPanel?.Uninstall();
        editor.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
    }

    public void Open(bool replace = false)
    {
        if (_editor == null || Session == null)
            return;
        Session.Open(_editor.SelectionStart, _editor.SelectionLength, _editor.CaretOffset);
        var box = replace ? ReplaceBox : FindBox;
        box.Focus();
        box.SelectAll();
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control && e.Key is Key.F or Key.H)
        {
            Open(e.Key == Key.H);
            e.Handled = true;
        }
        else if (e.Key == Key.F3 && e.KeyModifiers == KeyModifiers.None)
        {
            Open();
            Session?.FindNextCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Session?.IsOpen == true)
        {
            Session.CloseCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnBarKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Session?.CloseCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key is Key.Enter or Key.F3 && e.Source is TextBox)
        {
            Session?.FindNextCommand.Execute(null);
            e.Handled = true;
        }
    }
}
