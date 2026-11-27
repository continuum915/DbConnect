using DbConnect.Services;

namespace DbConnect.Tests;

public class BlobViewerTests
{
    // ── ImageFormatDetector ──────────────────────────────────────────────────

    [Fact]
    public void ImageFormatDetector_PngBytes_ReturnsPng()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 };
        Assert.Equal("PNG", ImageFormatDetector.Detect(png));
    }

    [Fact]
    public void ImageFormatDetector_JpegBytes_ReturnsJpeg()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        Assert.Equal("JPEG", ImageFormatDetector.Detect(jpeg));
    }

    [Fact]
    public void ImageFormatDetector_GifBytes_ReturnsGif()
    {
        var gif = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 };
        Assert.Equal("GIF", ImageFormatDetector.Detect(gif));
    }

    [Fact]
    public void ImageFormatDetector_BmpBytes_ReturnsBmp()
    {
        var bmp = new byte[] { 0x42, 0x4D, 0x36, 0x00, 0x00, 0x00 };
        Assert.Equal("BMP", ImageFormatDetector.Detect(bmp));
    }

    [Fact]
    public void ImageFormatDetector_WebPBytes_ReturnsWebP()
    {
        var webp = new byte[] { 0x52, 0x49, 0x46, 0x46, 0xAA, 0xBB, 0xCC, 0xDD, 0x57, 0x45, 0x42, 0x50, 0x56 };
        Assert.Equal("WebP", ImageFormatDetector.Detect(webp));
    }

    [Fact]
    public void ImageFormatDetector_RandomBytes_ReturnsNull()
    {
        var random = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 };
        Assert.Null(ImageFormatDetector.Detect(random));
    }

    [Fact]
    public void ImageFormatDetector_EmptySpan_ReturnsNull()
    {
        Assert.Null(ImageFormatDetector.Detect(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void ImageFormatDetector_SingleByte_ReturnsNull()
    {
        Assert.Null(ImageFormatDetector.Detect(new byte[] { 0xFF }));
    }

    // ── HexDumpBuilder ───────────────────────────────────────────────────────

    [Fact]
    public void HexDumpBuilder_EmptyArray_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, HexDumpBuilder.Build(Array.Empty<byte>()));
    }

    [Fact]
    public void HexDumpBuilder_ShortArray_FormatsCorrectly()
    {
        var data = new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F };
        var dump = HexDumpBuilder.Build(data);

        Assert.Contains("0000", dump);
        Assert.Contains("48 65 6C 6C 6F", dump);
        Assert.Contains("Hello", dump);
    }

    [Fact]
    public void HexDumpBuilder_ExactRowBoundary_NoTrailingPaddingOnLastRow()
    {
        // 16 bytes exactly fills one row — no padding hex columns needed
        var data = new byte[16];
        var dump = HexDumpBuilder.Build(data);
        var lines = dump.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
    }

    [Fact]
    public void HexDumpBuilder_OverMaxBytes_AppendsEllipsis()
    {
        var data = new byte[5000];
        var dump = HexDumpBuilder.Build(data, maxBytes: 4096);
        Assert.Contains("more bytes not shown", dump);
    }

    [Fact]
    public void HexDumpBuilder_NonPrintableBytes_ShowsDots()
    {
        var data = new byte[] { 0x01, 0x02, 0x7F, 0x80 };
        var dump = HexDumpBuilder.Build(data);
        // Non-printable bytes (outside 0x20–0x7E) should be represented as '.'
        Assert.Contains("....", dump);
    }

    [Fact]
    public void HexDumpBuilder_PrintableAscii_ShowsChars()
    {
        var data = System.Text.Encoding.ASCII.GetBytes("ABCD");
        var dump = HexDumpBuilder.Build(data);
        Assert.Contains("ABCD", dump);
    }
}
