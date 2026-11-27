using System;
using System.Text;

namespace DbConnect.Services;

public static class HexDumpBuilder
{
    private const int BytesPerRow = 16;

    public static string Build(byte[] data, int maxBytes = 4096)
    {
        if (data.Length == 0) return string.Empty;

        var sb = new StringBuilder();
        var limit = Math.Min(data.Length, maxBytes);

        for (int offset = 0; offset < limit; offset += BytesPerRow)
        {
            sb.Append($"{offset:X4}  ");

            var rowLen = Math.Min(BytesPerRow, limit - offset);
            for (int i = 0; i < BytesPerRow; i++)
            {
                if (i < rowLen)
                    sb.Append($"{data[offset + i]:X2} ");
                else
                    sb.Append("   ");
                if (i == 7) sb.Append(' ');
            }

            sb.Append(' ');
            for (int i = 0; i < rowLen; i++)
            {
                var b = data[offset + i];
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }

            sb.AppendLine();
        }

        if (data.Length > maxBytes)
            sb.Append($"\n… {data.Length - maxBytes:N0} more bytes not shown");

        return sb.ToString();
    }
}
