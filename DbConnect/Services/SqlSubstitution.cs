using System.Collections.Generic;

namespace DbConnect.Services;

public sealed record SqlSubstitution(string Sql, IReadOnlyList<int> SourceOffsets);
