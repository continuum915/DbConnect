using Avalonia.Media.Imaging;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using System;
using System.Linq;
using Avalonia.Media;

namespace DbConnect.Models;

public enum CompletionKind { Keyword, Table, View, Column, Function, Schema }

public class SqlCompletionData : ICompletionData
{
    private readonly CompletionKind _kind;
    private readonly string _insertionText;
    private readonly SchemaIndex? _index;

    public SqlCompletionData(string text, CompletionKind kind, string? displayText = null, SchemaIndex? index = null, string? insertionText = null)
    {
        Text = text;
        _insertionText = insertionText ?? text;
        _kind = kind;
        _index = index;
        Content = displayText ?? text;
    }

    public IImage? Image => BuildIcon(_kind);

    public string Text { get; }

    public object Content { get; }

    public object Description => _kind switch
    {
        CompletionKind.Table => $"Table: {Text}",
        CompletionKind.View => $"View: {Text}",
        CompletionKind.Column => BuildColumnDescription(),
        CompletionKind.Keyword => $"SQL keyword",
        CompletionKind.Function => $"Function: {Text}",
        CompletionKind.Schema => $"Schema: {Text}",
        _ => Text
    };

    public double Priority => _kind == CompletionKind.Keyword ? 0.5 : 1.0;

    private string BuildColumnDescription()
    {
        if (_index == null) return $"Column: {Text}";

        var owners = _index.TableColumns
            .Where(kv => kv.Value.Contains(Text))
            .Select(kv => kv.Key)
            .Take(3)
            .ToList();

        if (owners.Count == 0) return $"Column: {Text}";
        return $"Column: {Text}\nIn: {string.Join(", ", owners)}";
    }

    private static IImage? BuildIcon(CompletionKind kind)
    {
        var geometryStr = kind switch
        {
            CompletionKind.Keyword => PathGeometries.Diamond,
            CompletionKind.Table => PathGeometries.Grid,
            CompletionKind.View => PathGeometries.Layers,
            CompletionKind.Column => PathGeometries.Lines,
            CompletionKind.Function => PathGeometries.Circle,
            CompletionKind.Schema => PathGeometries.Folder,
            _ => null
        };

        if (geometryStr == null) return null;

        return new DrawingImage
        {
            Drawing = new GeometryDrawing
            {
                Geometry = Geometry.Parse(geometryStr),
                Brush = kind switch
                {
                    CompletionKind.Keyword => Brushes.CornflowerBlue,
                    CompletionKind.Table => Brushes.MediumSeaGreen,
                    CompletionKind.View => Brushes.Orchid,
                    CompletionKind.Column => Brushes.SandyBrown,
                    CompletionKind.Function => Brushes.SteelBlue,
                    CompletionKind.Schema => Brushes.Goldenrod,
                    _ => Brushes.Gray
                }
            }
        };
    }

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        textArea.Document.Replace(completionSegment, _insertionText);
    }
}
