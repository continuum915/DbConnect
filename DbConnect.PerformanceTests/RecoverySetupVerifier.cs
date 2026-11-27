using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DbConnect.PerformanceTests;

/// <summary>Checks the fixed recovery schema and every SQL character without retaining decoded SQL.</summary>
public static class RecoverySetupVerifier
{
    public static bool Matches(Stream source, string expectedSql)
    {
        try
        {
            using var reader = new StreamReader(source, new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
            var json = new Reader(reader);
            json.Expect('{');
            json.Property("Pid");
            if (json.Number() != Environment.ProcessId.ToString(CultureInfo.InvariantCulture)) return false;
            json.Expect(',');
            json.Property("Timestamp");
            // Use the legacy reader's scalar converters for bounded metadata, never for QueryText.
            _ = JsonSerializer.Deserialize<DateTimeOffset>(JsonSerializer.Serialize(json.Text()));
            json.Expect(',');
            json.Property("Tabs");
            json.Expect('[');
            json.Expect('{');
            json.Property("Title");
            json.NullableText();
            json.Expect(',');
            json.Property("QueryText");
            if (!json.MatchesText(expectedSql)) return false;
            json.Expect(',');
            json.Property("SessionName");
            json.NullableText();
            json.Expect(',');
            json.Property("ConnectionId");
            var connection = json.NullableText();
            if (connection != null) _ = JsonSerializer.Deserialize<Guid>(JsonSerializer.Serialize(connection));
            json.Expect(',');
            json.Property("FilePath");
            json.NullableText();
            json.Expect(',');
            json.Property("IsDirty");
            json.Boolean();
            json.Expect('}');
            json.Expect(']'); // Exactly one setup tab, as required by the previous oracle.
            json.Expect(',');
            json.Property("ActiveTabTitle");
            json.NullableText();
            json.Expect('}');
            return json.End;
        }
        catch (Exception exception) when (exception is InvalidDataException or DecoderFallbackException or JsonException)
        {
            return false;
        }
    }

    private sealed class Reader(StreamReader source)
    {
        public bool End { get { WhiteSpace(); return source.Peek() < 0; } }

        public void Expect(char expected)
        {
            WhiteSpace();
            if (source.Read() != expected) throw new InvalidDataException("Unexpected recovery JSON token.");
        }

        public void Property(string name)
        {
            if (Text() != name) throw new InvalidDataException("Unexpected recovery property.");
            Expect(':');
        }

        public string Number()
        {
            WhiteSpace();
            var text = new StringBuilder();
            while (source.Peek() is '-' or >= '0' and <= '9')
            {
                if (text.Length >= 16) throw new InvalidDataException("Oversized recovery number.");
                text.Append((char)source.Read());
            }
            return text.ToString();
        }

        public bool Boolean()
        {
            WhiteSpace();
            var literal = source.Peek() == 't' ? "true" : "false";
            foreach (var ch in literal)
                if (source.Read() != ch) throw new InvalidDataException("Invalid boolean value.");
            return literal == "true";
        }

        public string? NullableText()
        {
            WhiteSpace();
            if (source.Peek() != 'n') return Text();
            foreach (var ch in "null")
                if (source.Read() != ch) throw new InvalidDataException("Invalid null value.");
            return null;
        }

        public string Text()
        {
            Expect('"');
            var text = new StringBuilder();
            int ch;
            while ((ch = Character()) >= 0)
            {
                if (text.Length >= 4096) throw new InvalidDataException("Oversized recovery metadata.");
                text.Append((char)ch);
            }
            return text.ToString();
        }

        public bool MatchesText(string expected)
        {
            Expect('"');
            var index = 0;
            int ch;
            while ((ch = Character()) >= 0)
                if (index >= expected.Length || expected[index++] != ch) return false;
            return index == expected.Length;
        }

        private int Character()
        {
            var ch = source.Read();
            if (ch == '"') return -1;
            if (ch < 0x20) throw new InvalidDataException("Invalid recovery JSON string.");
            if (ch != '\\') return ch;
            return source.Read() switch
            {
                '"' => '"',
                '\\' => '\\',
                '/' => '/',
                'b' => '\b',
                'f' => '\f',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                'u' => Unicode(),
                _ => throw new InvalidDataException("Invalid recovery JSON escape.")
            };
        }

        private int Unicode()
        {
            var value = 0;
            for (var i = 0; i < 4; i++)
            {
                var ch = source.Read();
                var digit = ch switch
                {
                    >= '0' and <= '9' => ch - '0',
                    >= 'a' and <= 'f' => ch - 'a' + 10,
                    >= 'A' and <= 'F' => ch - 'A' + 10,
                    _ => -1
                };
                if (digit < 0) throw new InvalidDataException("Invalid recovery Unicode escape.");
                value = value * 16 + digit;
            }
            return value;
        }

        private void WhiteSpace()
        {
            while (source.Peek() is ' ' or '\t' or '\r' or '\n') source.Read();
        }
    }
}
