using System.Text;
using System.Text.Json;
using DbConnect.Services;
using DbConnect.ViewModels;

namespace DbConnect.Tests;

/// <summary>RAIN-P2-18 — The read-only cell value viewer: size tiers, the lazy JSON tree and Save to file.</summary>
public class CellValueViewerTests
{
    [Theory]
    [InlineData(0, CellViewerTier.Full)]
    [InlineData(CellViewerLimits.FullMaxChars, CellViewerTier.Full)]
    [InlineData(CellViewerLimits.FullMaxChars + 1, CellViewerTier.Reduced)]
    [InlineData(CellViewerLimits.ReducedMaxChars, CellViewerTier.Reduced)]
    [InlineData(CellViewerLimits.ReducedMaxChars + 1, CellViewerTier.Preview)]
    public void TierFor_SplitsAtTheThresholds(int length, CellViewerTier expected) =>
        Assert.Equal(expected, CellViewerLimits.TierFor(length));

    [Fact]
    public void TextOf_KeepsStringsExactly_AndLeavesNullAndBinaryToOtherViewers()
    {
        Assert.Equal("{ \"a\":1 }", CellValueViewerViewModel.TextOf("{ \"a\":1 }"));
        Assert.Equal("42", CellValueViewerViewModel.TextOf(42));
        Assert.Null(CellValueViewerViewModel.TextOf(null));
        Assert.Null(CellValueViewerViewModel.TextOf(new byte[] { 1 }));
    }

    [Fact]
    public void Tree_StartsWithTheRootExpanded_AndBuildsNothingForCollapsedNodes()
    {
        using var doc = JsonDocument.Parse("""{"name":"x","tags":[1,2],"nested":{"deep":{"deeper":true}}}""");
        var tree = new JsonTreeViewModel(doc.RootElement);

        Assert.Equal(["(root)", "name", "tags", "nested"], tree.Rows.Select(row => row.Label));
        Assert.Equal([0, 1, 1, 1], tree.Rows.Select(row => row.Depth));
        Assert.Equal(["{3}", "\"x\"", "[2]", "{1}"], tree.Rows.Select(row => row.Value));
        Assert.False(tree.Rows[1].IsExpandable);
        Assert.All(tree.Rows.Skip(2), row => Assert.False(row.IsExpanded));
    }

    [Fact]
    public void Toggle_InsertsChildrenBelowTheRow_AndCollapsingRemovesTheWholeSubtree()
    {
        using var doc = JsonDocument.Parse("""{"nested":{"deep":{"deeper":true}},"after":null}""");
        var tree = new JsonTreeViewModel(doc.RootElement);

        tree.Toggle(tree.Rows[1]);
        tree.Toggle(tree.Rows[2]);

        Assert.Equal(["(root)", "nested", "deep", "deeper", "after"], tree.Rows.Select(row => row.Label));
        Assert.Equal([0, 1, 2, 3, 1], tree.Rows.Select(row => row.Depth));
        Assert.Equal("true", tree.Rows[3].Value);

        tree.Toggle(tree.Rows[1]);

        Assert.Equal(["(root)", "nested", "after"], tree.Rows.Select(row => row.Label));
        Assert.False(tree.Rows[1].IsExpanded);
    }

    [Fact]
    public void ArraysOverOneHundredChildren_AreShownAsRangeGroups_EachExpandedOnDemand()
    {
        var json = "[" + string.Join(",", Enumerable.Range(0, 250)) + "]";
        using var doc = JsonDocument.Parse(json);
        var tree = new JsonTreeViewModel(doc.RootElement);

        Assert.Equal(["(root)", "[0…99]", "[100…199]", "[200…249]"], tree.Rows.Select(row => row.Label));

        tree.Toggle(tree.Rows[2]);

        Assert.Equal(1 + 3 + 100, tree.Rows.Count);
        Assert.Equal("[100]", tree.Rows[3].Label);
        Assert.Equal("100", tree.Rows[3].Value);
        Assert.Equal("[199]", tree.Rows[102].Label);
        Assert.Equal("[200…249]", tree.Rows[103].Label);
    }

    [Fact]
    public void ObjectsOverOneHundredProperties_AreGroupedToo()
    {
        var json = "{" + string.Join(",", Enumerable.Range(0, 101).Select(i => $"\"k{i}\":{i}")) + "}";
        using var doc = JsonDocument.Parse(json);
        var tree = new JsonTreeViewModel(doc.RootElement);

        Assert.Equal(["(root)", "[0…99]", "[100…100]"], tree.Rows.Select(row => row.Label));
        tree.Toggle(tree.Rows[2]);
        Assert.Equal("k100", tree.Rows[3].Label);
    }

    [Fact]
    public void ExactlyOneHundredChildren_AreNotGrouped()
    {
        using var doc = JsonDocument.Parse("[" + string.Join(",", Enumerable.Range(0, 100)) + "]");
        var tree = new JsonTreeViewModel(doc.RootElement);

        Assert.Equal(101, tree.Rows.Count);
        Assert.Equal("[99]", tree.Rows[100].Label);
    }

    [Fact]
    public void LongStrings_AreTruncatedInTheTree_AndCanBeOpenedInFull()
    {
        var shortText = new string('s', CellViewerLimits.TreeStringMaxChars);
        var longText = new string('a', CellViewerLimits.TreeStringMaxChars) + "TAIL";
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { shortText, longText }));
        var tree = new JsonTreeViewModel(doc.RootElement);
        string? opened = null;
        tree.OpenStringRequested += (_, text) => opened = text;

        var shortRow = tree.Rows[1];
        var longRow = tree.Rows[2];

        Assert.False(shortRow.IsTruncated);
        Assert.Equal($"\"{shortText}\"", shortRow.Value);
        Assert.True(longRow.IsTruncated);
        Assert.Equal($"\"{new string('a', CellViewerLimits.TreeStringMaxChars)}…\"", longRow.Value);

        longRow.OpenFullStringCommand.Execute(null);
        Assert.Equal(longText, opened);
    }

    [Fact]
    public async Task SaveTo_WritesTheOriginalValueUnchanged()
    {
        const string value = "{\"b\":  2,\n \"a\":\"héllo 🌧\"}";
        using var viewer = new CellValueViewerViewModel(value, "payload");
        await viewer.LoadAsync();
        viewer.FormatJson = true;

        using var stream = new MemoryStream();
        await viewer.SaveToAsync(stream);

        Assert.Equal(Encoding.UTF8.GetBytes(value), stream.ToArray());
        Assert.Equal("payload.json", viewer.SuggestedFileName);
    }

    [Fact]
    public async Task ValidJson_OpensInTheTree_WithFormatOnByDefault()
    {
        using var viewer = new CellValueViewerViewModel("{\"a\":[1,2]}", "c");
        Assert.True(viewer.IsParsing);

        await viewer.LoadAsync();

        Assert.False(viewer.IsParsing);
        Assert.True(viewer.CanShowTree);
        Assert.True(viewer.IsTreeView);
        Assert.True(viewer.CanFormat);
        Assert.True(viewer.FormatJson);
        Assert.True(viewer.IsHighlightingEnabled);
        Assert.Contains("\n", viewer.DisplayText);

        viewer.FormatJson = false;
        Assert.Equal("{\"a\":[1,2]}", viewer.DisplayText);
    }

    [Fact]
    public async Task InvalidJson_FallsBackToTheTextView_WithTheParsersLineAndPosition()
    {
        using var viewer = new CellValueViewerViewModel("{\n  \"a\": 1,\n  oops\n}", "c");
        await viewer.LoadAsync();

        Assert.False(viewer.CanShowTree);
        Assert.False(viewer.IsTreeView);
        Assert.Equal("Not valid JSON: line 3, position 3", viewer.ParseBanner);
        Assert.Equal("{\n  \"a\": 1,\n  oops\n}", viewer.DisplayText);
        Assert.Equal("c.txt", viewer.SuggestedFileName);
    }

    [Theory]
    [InlineData("{\n  \"é😀\": oops\n}", "line 2, position 10")]
    [InlineData("{\r\n  \"é😀\": oops\r\n}", "line 2, position 10")]
    [InlineData("[\"日本\", oops]", "line 1, position 8")]
    public async Task InvalidJson_PositionCountsCharactersNotUtf8Bytes(string value, string expected)
    {
        using var viewer = new CellValueViewerViewModel(value, "c");
        await viewer.LoadAsync();

        Assert.Equal($"Not valid JSON: {expected}", viewer.ParseBanner);
    }

    [Fact]
    public async Task PlainText_IsNotFlaggedAsInvalidJson()
    {
        using var viewer = new CellValueViewerViewModel("just some text", "c");
        await viewer.LoadAsync();

        Assert.Null(viewer.ParseBanner);
        Assert.False(viewer.CanShowTree);
        Assert.Equal("just some text", viewer.DisplayText);
    }

    [Fact]
    public async Task ReducedTierJson_IsAlwaysFormatted_WithoutHighlighting()
    {
        var json = "[" + string.Join(",", Enumerable.Repeat("\"" + new string('x', 1000) + "\"", 1100)) + "]";
        Assert.Equal(CellViewerTier.Reduced, CellViewerLimits.TierFor(json.Length));
        using var viewer = new CellValueViewerViewModel(json, "c");
        await viewer.LoadAsync();

        Assert.True(viewer.CanShowTree);
        Assert.False(viewer.CanFormat);
        Assert.False(viewer.IsHighlightingEnabled);
        Assert.Equal("Large value: highlighting disabled.", viewer.SizeBanner);
        Assert.Contains("\n", viewer.DisplayText);
    }

    [Fact]
    public async Task ReducedTierTextWithAVeryLongLine_ForcesWordWrapOn()
    {
        var text = new string('x', CellViewerLimits.FullMaxChars + 10);
        using var viewer = new CellValueViewerViewModel(text, "c");
        await viewer.LoadAsync();

        Assert.True(viewer.IsWordWrapForced);
        Assert.True(viewer.WordWrap);
    }

    [Fact]
    public async Task PreviewTier_ShowsTheFirst64KB_ButSaveStillWritesEverything()
    {
        var text = "[" + new string('1', CellViewerLimits.ReducedMaxChars) + "]";
        using var viewer = new CellValueViewerViewModel(text, "big");
        await viewer.LoadAsync();

        Assert.Equal(CellViewerTier.Preview, viewer.Tier);
        Assert.False(viewer.CanShowTree);
        Assert.Equal(CellViewerLimits.PreviewChars, viewer.DisplayText!.Length);
        Assert.Contains(text.Length.ToString("N0"), viewer.SizeBanner);
        Assert.Same(text, viewer.Value);

        using var stream = new MemoryStream();
        await viewer.SaveToAsync(stream);
        Assert.Equal(text.Length, stream.Length);
    }

    [Fact]
    public void Dispose_BeforeParsingFinishes_CancelsTheLoad()
    {
        var viewer = new CellValueViewerViewModel("{\"a\":1}", "c");
        viewer.Dispose();

        var load = viewer.LoadAsync();

        Assert.True(load.IsCompleted);
        Assert.Null(viewer.Tree);
    }

    [Fact]
    public async Task OpeningAStringFromTheTree_ShowsItInTheTextView_UntilTheWholeValueIsShownAgain()
    {
        var longText = new string('z', 300);
        using var viewer = new CellValueViewerViewModel(JsonSerializer.Serialize(new { longText }), "c");
        await viewer.LoadAsync();

        viewer.Tree!.Rows[1].OpenFullStringCommand.Execute(null);

        Assert.False(viewer.IsTreeView);
        Assert.Equal(longText, viewer.DisplayText);
        Assert.True(viewer.IsShowingString);

        viewer.ShowWholeValueCommand.Execute(null);
        Assert.False(viewer.IsShowingString);
        Assert.StartsWith("{", viewer.DisplayText);
    }

    [Fact]
    public async Task OpeningAStringWithAVeryLongLine_TurnsWordWrapOn()
    {
        using var viewer = new CellValueViewerViewModel(JsonSerializer.Serialize(new { s = new string('q', CellViewerLimits.LongLineChars + 1) }), "c");
        await viewer.LoadAsync();
        Assert.False(viewer.WordWrap);

        viewer.Tree!.Rows[1].OpenFullStringCommand.Execute(null);

        Assert.True(viewer.WordWrap);
    }

    [Fact]
    public void TreeTruncation_DoesNotSplitASurrogatePair()
    {
        var text = new string('a', CellViewerLimits.TreeStringMaxChars - 1) + "🌧" + "rest";
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { text }));

        var row = new JsonTreeViewModel(doc.RootElement).Rows[1];

        Assert.Equal($"\"{new string('a', CellViewerLimits.TreeStringMaxChars - 1)}…\"", row.Value);
    }

    [Fact]
    public void JsonFoldings_SpanMultiLineBrackets_AndIgnoreBracketsInStrings()
    {
        const string text = "{\n  \"a\": [\n    \"}{\"\n  ],\n  \"b\": [1]\n}";

        var foldings = JsonFoldingRanges.Compute(text);

        Assert.Equal([(0, text.Length - 1), (text.IndexOf('['), text.IndexOf(']'))],
            foldings.OrderBy(f => f.Start).Select(f => (f.Start, f.End)));
    }
}
