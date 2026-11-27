using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using AvaloniaEdit;
using DbConnect.Services;
using DbConnect.ViewModels;
using DbConnect.Views;

namespace DbConnect.Tests;

/// <summary>RAIN-P2-18 — The value viewer window, rendered headless.</summary>
public class CellValueViewerHeadlessTests
{
    [AvaloniaFact]
    public async Task AFiveMegabyteMinifiedJsonValue_OpensWithoutBlockingTheUiThread_InBothViews()
    {
        var json = "[" + string.Join(",", Enumerable.Range(0, 160_000).Select(i => $"{{\"id\":{i},\"name\":\"item {i}\"}}")) + "]";
        Assert.True(json.Length > 5_000_000 && !json.Contains('\n'));

        var viewModel = new CellValueViewerViewModel(json, "payload");
        var window = new CellValueViewerWindow(viewModel);

        window.Show();
        Assert.True(viewModel.IsParsing);
        Assert.True(window.IsVisible);

        await WaitForParseAsync(viewModel);

        Assert.True(viewModel.IsTreeView);
        Assert.Equal(1 + 1600, viewModel.Tree!.Rows.Count);

        viewModel.ShowTextCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var editor = window.FindControl<TextEditor>("Editor")!;
        Assert.True(editor.IsVisible);
        await WaitUntilAsync(() => editor.Document.LineCount > 1); // the formatted document is built off the UI thread
        Assert.Equal(viewModel.DisplayText, editor.Text);
        Assert.DoesNotContain(editor.TextArea.CommandBindings, binding => binding.Command == ApplicationCommands.Find);

        window.Close();
    }

    [AvaloniaFact]
    public async Task InvalidJson_ShowsTheBannerAndTheTextView()
    {
        var viewModel = new CellValueViewerViewModel("[1, 2,", "c");
        var window = new CellValueViewerWindow(viewModel);
        window.Show();

        await WaitForParseAsync(viewModel);

        Assert.True(window.FindControl<TextEditor>("Editor")!.IsVisible);
        Assert.StartsWith("Not valid JSON: line 1, position", window.FindControl<TextBlock>("ParseBannerText")!.Text);
        window.Close();
    }

    private static async Task WaitForParseAsync(CellValueViewerViewModel viewModel)
    {
        await WaitUntilAsync(() => !viewModel.IsParsing);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var wait = Stopwatch.StartNew();
        while (!condition() && wait.Elapsed < TimeSpan.FromSeconds(30))
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.True(condition());
    }
}
