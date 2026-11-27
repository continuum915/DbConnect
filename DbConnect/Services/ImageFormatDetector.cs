using System;

namespace DbConnect.Services;

public static class ImageFormatDetector
{
    public static string? Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return null;

        if (data.Length >= 4 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            return "PNG";
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return "JPEG";
        if (data.Length >= 3 && data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46)
            return "GIF";
        if (data[0] == 0x42 && data[1] == 0x4D)
            return "BMP";
        if (data.Length >= 12 &&
            data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
            data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
            return "WebP";

        return null;
    }
}
