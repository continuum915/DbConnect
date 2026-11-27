using DbConnect.PerformanceTests;

namespace DbConnect.Tests;

public sealed class EditorTypingPerformanceTests
{
    [Theory]
    [InlineData("editor.typing.start", 0)]
    [InlineData("editor.typing.middle", 2048)]
    [InlineData("editor.typing.end", 4096)]
    public async Task SupervisedTypingValidatesInputAndReportsPosition(string scenario, int offset)
    {
        var run = await new ProcessSupervisor().RunAsync(new(scenario, "representative", 4096),
            1, false, TimeSpan.FromSeconds(30));
        Assert.Equal("completed", run.Outcome);
        Assert.NotNull(run.Editor);
        Assert.Equal("headless-text-input", run.Editor.InputMethod);
        Assert.Equal(offset, run.Editor.InputOffset);
        Assert.Equal(4097, run.Editor.DocumentCharacters);
        Assert.False(run.Editor.HighlightingCompletionClaimed);
        Assert.True(run.Editor.Dispatcher.SampleCount >= 2);
        Assert.False(Directory.Exists(run.Editor.IsolatedDirectory));
    }
}
