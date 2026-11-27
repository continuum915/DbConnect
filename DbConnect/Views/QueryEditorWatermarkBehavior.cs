using System;
using Avalonia;
using Avalonia.Controls;
using AvaloniaEdit;

namespace DbConnect.Views;

/// <summary>Avoids the pinned theme's full Document.Text read for its empty-document watermark.</summary>
internal static class QueryEditorWatermarkBehavior
{
    private static readonly StyledProperty<bool> IsEmptyProperty =
        AvaloniaProperty.Register<TextEditor, bool>("DbConnectQueryIsEmpty", true);

    public static void Attach(TextEditor editor)
    {
        IDisposable? binding = null;
        void Update() => editor.SetValue(IsEmptyProperty, editor.Document == null || editor.Document.TextLength == 0);
        editor.TextArea.TemplateApplied += (_, e) =>
        {
            binding?.Dispose();
            Update();
            // Replacing the binding detaches its Document.Text observer, including when visibility is unchanged.
            if (e.NameScope.Find("PART_Watermark") is TextBlock watermark)
                binding = watermark.Bind(TextBlock.IsVisibleProperty, editor.GetObservable(IsEmptyProperty));
        };
        editor.TextChanged += (_, _) => Update();
        editor.DocumentChanged += (_, _) => Update();
    }
}
