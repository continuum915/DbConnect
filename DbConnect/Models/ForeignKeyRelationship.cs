using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DbConnect.Models;

public sealed record ForeignKeyRelationship(string Name, string Table, string ReferencedTable,
    IReadOnlyList<string> Columns, IReadOnlyList<string> ReferencedColumns);

public sealed record ForeignKeyLookupValue(IReadOnlyList<object?> Values)
{
    public override string ToString() => string.Join(" · ", Values.Select(value => value switch
    {
        null or DBNull => "NULL",
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    }));
}
