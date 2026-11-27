using DbConnect.Services;

namespace DbConnect.PerformanceTests;

public static class ParameterValidation
{
    public static void Validate(ImportFixture fixture, SqlSubstitution result)
    {
        const string placeholder = "@value";
        const string literal = "'D''Angelo'";
        if (result.SourceOffsets.Count != result.Sql.Length)
            throw new InvalidDataException("Substituted SQL and source mapping lengths differ.");
        var cursor = 0;
        var output = 0;
        // Independent generator markers locate the real expression, not a production parser.
        foreach (var insert in fixture.Expected.Where(insert => insert.Table == "Parents"))
        {
            var start = insert.Start + insert.Length - placeholder.Length - 1; // closing )
            if (!fixture.Sql.AsSpan(start, placeholder.Length).SequenceEqual(placeholder))
                throw new InvalidDataException("Missing generated parameter marker.");
            CheckOriginal(cursor, start);
            CheckText(literal);
            for (var i = 0; i < literal.Length; i++)
                if (result.SourceOffsets[output + i] != start) Fail();
            output += literal.Length;
            cursor = start + placeholder.Length;
        }
        CheckOriginal(cursor, fixture.Sql.Length);
        if (output != result.Sql.Length) Fail();

        void CheckOriginal(int start, int end)
        {
            CheckText(fixture.Sql.AsSpan(start, end - start));
            for (var i = start; i < end; i++)
                if (result.SourceOffsets[output + i - start] != i) Fail();
            output += end - start;
        }

        void CheckText(ReadOnlySpan<char> expected)
        {
            if (output > result.Sql.Length - expected.Length ||
                !result.Sql.AsSpan(output, expected.Length).SequenceEqual(expected)) Fail();
        }

        static void Fail() => throw new InvalidDataException("Substituted SQL or source mappings differ from independent markers.");
    }
}
