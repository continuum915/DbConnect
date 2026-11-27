using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DbConnect.Services;

// Validates syntax and record width. The caller owns decoded input and header/type policies.
internal sealed class CsvRecordReader(TextReader input, char delimiter)
{
    private int physicalLine = 1;
    private int logicalRecord = 1;
    private int? width;
    private bool previousWasCr;

    public string[]? ReadRecord()
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;
        bool closedQuote = false;
        bool hasContent = false;

        while (true)
        {
            int line = physicalLine;
            int next = ReadCharacter();
            if (next < 0)
            {
                if (inQuotes) throw Error("Unclosed quoted field", physicalLine, fields.Count + 1);
                if (!hasContent) return null;
                fields.Add(field.ToString());
                return CompleteRecord(fields, physicalLine);
            }

            char c = (char)next;
            if (!inQuotes && (c == '\r' || c == '\n'))
            {
                if (c == '\r' && input.Peek() == '\n') ReadCharacter();
                if (!hasContent) continue;
                fields.Add(field.ToString());
                return CompleteRecord(fields, line);
            }

            hasContent = true;
            if (closedQuote && c != delimiter)
                throw Error("Unexpected character after closing quote", line, fields.Count + 1);
            if (c == '"')
            {
                if (inQuotes && input.Peek() == '"')
                {
                    ReadCharacter();
                    field.Append('"');
                }
                else
                {
                    if (!inQuotes && field.Length > 0)
                        throw Error("Unexpected quote in unquoted field", line, fields.Count + 1);
                    closedQuote = inQuotes;
                    inQuotes = !inQuotes;
                }
            }
            else if (c == delimiter && !inQuotes)
            {
                fields.Add(field.ToString());
                field.Clear();
                closedQuote = false;
            }
            else
            {
                field.Append(c);
            }
        }
    }

    private int ReadCharacter()
    {
        int c = input.Read();
        if (c == '\r' || (c == '\n' && !previousWasCr)) physicalLine++;
        previousWasCr = c == '\r';
        return c;
    }

    private string[] CompleteRecord(List<string> fields, int line)
    {
        if (width is int expected && fields.Count != expected)
            throw Error($"Record width mismatch: expected {expected} fields but found {fields.Count}",
                line, fields.Count < expected ? fields.Count + 1 : expected + 1);
        width ??= fields.Count;
        logicalRecord++;
        return fields.ToArray();
    }

    private FormatException Error(string reason, int line, int column) =>
        new($"CSV record {logicalRecord}, physical line {line}, field column {column}: {reason}.");
}
