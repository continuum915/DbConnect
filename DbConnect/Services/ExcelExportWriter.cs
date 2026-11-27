using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using DbConnect.Models;

namespace DbConnect.Services;

internal sealed class ExcelExportException(string message) : Exception(message);

/// <summary>Forward-only XLSX with inline strings and a fixed style table.</summary>
internal static class ExcelExportWriter
{
    private const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static async Task<string?> WriteAsync(Stream stream, IExportSource source, IReadOnlyList<ExportColumn> columns,
        bool headers, Action rowWritten, CancellationToken token)
    {
        if (columns.Count > 16384) throw new ExcelExportException("Excel supports at most 16384 columns.");
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        Add(archive, "[Content_Types].xml", """
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>
            """);
        Add(archive, "_rels/.rels", """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
            """);
        Add(archive, "xl/workbook.xml", """
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Results" sheetId="1" r:id="rId1"/></sheets></workbook>
            """);
        Add(archive, "xl/_rels/workbook.xml.rels", """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
            """);
        Add(archive, "xl/styles.xml", """
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><numFmts count="1"><numFmt numFmtId="164" formatCode="yyyy-mm-dd hh:mm:ss.000"/></numFmts><fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="164" applyNumberFormat="1"/></cellXfs></styleSheet>
            """);
        using var sheet = archive.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest).Open();
        using var xml = XmlWriter.Create(sheet, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            NewLineHandling = NewLineHandling.Entitize
        });
        xml.WriteStartElement("worksheet", Ns);
        xml.WriteStartElement("sheetData", Ns);
        long rowIndex = 0, fallbacks = 0;
        if (headers && columns.Count > 0)
        {
            xml.WriteStartElement("row", Ns);
            xml.WriteAttributeString("r", (++rowIndex).ToString(Invariant));
            for (var c = 0; c < columns.Count; c++) WriteCell(xml, columns[c].Name, rowIndex, c, ref fallbacks);
            xml.WriteEndElement();
        }
        await foreach (var row in source.ReadRowsAsync(token).WithCancellation(token))
        {
            token.ThrowIfCancellationRequested();
            if (++rowIndex > 1048576) throw new ExcelExportException("Excel supports at most 1048576 worksheet rows, including headers.");
            xml.WriteStartElement("row", Ns);
            xml.WriteAttributeString("r", rowIndex.ToString(Invariant));
            for (var c = 0; c < columns.Count; c++)
            {
                token.ThrowIfCancellationRequested();
                WriteCell(xml, c < row.Count ? row[c] : null, rowIndex, c, ref fallbacks);
            }
            xml.WriteEndElement();
            if (rowIndex <= 3 || rowIndex % 256 == 0)
            {
                xml.Flush();
                await sheet.FlushAsync(token);
                await stream.FlushAsync(token);
            }
            rowWritten();
        }
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.Flush();
        token.ThrowIfCancellationRequested();
        return fallbacks == 0 ? null : $"{fallbacks} Excel cells stored as lossless text (precision, temporal or binary values).";
    }

    private static void Add(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void WriteCell(XmlWriter xml, object? value, long row, int column, ref long fallbacks)
    {
        if (value is null or DBNull) return;
        string text;
        var type = "inlineStr";
        var dateStyle = false;
        if (value is DateOnly day)
        {
            if (day.Year < 1900) { value = day.ToString("yyyy-MM-dd", Invariant); fallbacks++; }
            else value = day.ToDateTime(TimeOnly.MinValue);
        }
        switch (value)
        {
            case string s: text = s; break;
            case bool b: text = b ? "1" : "0"; type = "b"; break;
            case DateTime date:
                if (date.Kind == DateTimeKind.Unspecified && date >= new DateTime(1900, 1, 1)
                    && date.Ticks % TimeSpan.TicksPerMillisecond == 0 && DateTime.FromOADate(date.ToOADate()) == date)
                {
                    var serial = date.ToOADate();
                    if (date < new DateTime(1900, 3, 1)) serial--;
                    text = serial.ToString("R", Invariant); type = "n"; dateStyle = true;
                }
                else { text = date.ToString("O", Invariant); fallbacks++; }
                break;
            case DateTimeOffset date: text = date.ToString("O", Invariant); fallbacks++; break;
            case byte[] bytes: text = Convert.ToBase64String(bytes); fallbacks++; break;
            case Guid guid: text = guid.ToString(); fallbacks++; break;
            case TimeOnly time: text = time.ToString("O", Invariant); fallbacks++; break;
            case TimeSpan time: text = time.ToString("c", Invariant); fallbacks++; break;
            case sbyte or byte or short or ushort or int or uint or long or ulong or decimal or float or double:
                text = ((IFormattable)value).ToString(value is float or double ? "R" : null, Invariant);
                if (IsExcelNumber(text)) type = "n";
                else fallbacks++;
                break;
            default: throw new ExcelExportException($"Unsupported Excel value at row {row}, column {column + 1}.");
        }
        if (text.Length > 32767) throw new ExcelExportException($"Excel cell exceeds 32767 characters at row {row}, column {column + 1}.");
        var lineFeeds = 0;
        foreach (var character in text) if (character == '\n') lineFeeds++;
        if (lineFeeds > 253) throw new ExcelExportException($"Excel cell exceeds 253 line feeds at row {row}, column {column + 1}.");
        xml.WriteStartElement("c", Ns);
        var index = column + 1;
        var letters = "";
        while (index > 0) { index--; letters = (char)('A' + index % 26) + letters; index /= 26; }
        xml.WriteAttributeString("r", letters + row.ToString(Invariant));
        xml.WriteAttributeString("t", type);
        if (dateStyle) xml.WriteAttributeString("s", "1");
        if (type == "inlineStr")
        {
            xml.WriteStartElement("is", Ns);
            xml.WriteStartElement("t", Ns);
            xml.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
            xml.WriteString(EscapeText(text));
            xml.WriteEndElement();
            xml.WriteEndElement();
        }
        else xml.WriteElementString("v", Ns, text);
        xml.WriteEndElement();
    }

    private static bool IsExcelNumber(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, Invariant, out var number) || !double.IsFinite(number)
            || (number != 0 && Math.Abs(number) < 2.2250738585072014E-308)) return false;
        var mantissa = text.Split('E', 'e')[0].Replace("-", "").Replace(".", "").TrimStart('0').TrimEnd('0');
        return mantissa.Length <= 15;
    }

    private static string EscapeText(string text)
    {
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '_' && i + 6 < text.Length && text[i + 1] == 'x' && text[i + 6] == '_') result.Append("_x005F_");
            else if (c < 32 && c is not '\t' and not '\n' and not '\r' || c is '\uFFFE' or '\uFFFF') result.Append($"_x{(int)c:X4}_");
            else result.Append(c);
        }
        return result.ToString();
    }
}
