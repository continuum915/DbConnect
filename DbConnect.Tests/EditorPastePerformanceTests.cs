using System.Text.Json;
using DbConnect.PerformanceTests;

namespace DbConnect.Tests;

public sealed class EditorPastePerformanceTests
{
    [Fact]
    public async Task TimedOutEditorChildRetainsItsProfileLocationAndCleansItUp()
    {
        var run = await new ProcessSupervisor().RunAsync(new("editor.paste", "representative", 4096),
            1, false, TimeSpan.FromMilliseconds(1));
        Assert.Equal("timeout", run.Outcome);
        Assert.Null(run.Metrics);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(run));
        var directory = json.RootElement.GetProperty("EditorDataDirectory").GetString();
        Assert.False(string.IsNullOrWhiteSpace(directory));
        Assert.False(Directory.Exists(directory));
    }
    [Theory]
    [InlineData("editor.paste", "clipboard-paste")]
    [InlineData("editor.insert", "document-insert")]
    public async Task SupervisedPasteUsesTheRealEditorAndReportsSeparateUiPhases(string scenario, string inputMethod)
    {
        var run = await new ProcessSupervisor().RunAsync(new(scenario, "representative", 4096),
            1, false, TimeSpan.FromSeconds(30));
        Assert.Equal("completed", run.Outcome);
        Assert.NotNull(run.Fixture);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(run));
        var editor = json.RootElement.GetProperty("Editor");
        Assert.Equal(inputMethod, editor.GetProperty("InputMethod").GetString());
        Assert.Equal(run.Fixture.Sha256, editor.GetProperty("DocumentSha256").GetString());
        Assert.Equal(4096, editor.GetProperty("DocumentCharacters").GetInt32());
        Assert.True(editor.GetProperty("LayoutRenderOpportunityMilliseconds").GetDouble() >= 0);
        Assert.True(editor.GetProperty("Dispatcher").GetProperty("SampleCount").GetInt32() >= 2);
        Assert.False(editor.GetProperty("HighlightingCompletionClaimed").GetBoolean());
        Assert.False(Directory.Exists(editor.GetProperty("IsolatedDirectory").GetString()));
    }
}
