using DbConnect.Services;

namespace DbConnect.Tests;

public class GridCellDisplayTests
{
    [Fact]
    public void FiveMegabyteText_DisplaysOnlyABoundedPrefix()
    {
        var value = new string('x', 5 * 1024 * 1024);

        Assert.Equal(new string('x', 2048) + "…", GridCellDisplay.FormatValue(value));
    }

    [Theory]
    [InlineData("first\r\nsecond\nthird\rfourth", "first second third fourth")]
    [InlineData("", "")]
    [InlineData(" { \"a\": 1 } ", "{\"a\":1}")]
    public void ShortText_IsSingleLineAndKeepsExistingJsonFormatting(string value, string expected) =>
        Assert.Equal(expected, GridCellDisplay.FormatValue(value));

    [Fact]
    public void ExactLimit_IsNotMarkedAsTruncated()
    {
        var value = new string('a', 2048);
        Assert.Equal(value, GridCellDisplay.FormatValue(value));
        Assert.Equal(value + "…", GridCellDisplay.FormatValue(value + "b"));
    }

    [Fact]
    public void Truncation_DoesNotSplitASurrogatePair() =>
        Assert.Equal(new string('a', 2047) + "…", GridCellDisplay.FormatValue(new string('a', 2047) + "🌧tail"));

    [Fact]
    public void FiveMegabyteJson_DisplayDoesNotAllocateForTheWholeValue()
    {
        var value = "{\"payload\":\"" + new string('x', 5 * 1024 * 1024) + "\"}";
        GridCellDisplay.FormatValue(value);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var display = GridCellDisplay.FormatValue(value);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(value[..2048] + "…", display);
        Assert.True(allocated < 32 * 1024, $"Rendering a prefix allocated {allocated} bytes.");
    }
}
