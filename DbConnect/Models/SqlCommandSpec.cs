using System.Collections.Generic;

namespace DbConnect.Models;

public sealed class SqlCommandSpec
{
    public SqlCommandSpec(
        string commandText,
        IReadOnlyDictionary<string, object?> parameters,
        int? expectedRecordsAffected = null)
    {
        CommandText = commandText;
        Parameters = parameters;
        ExpectedRecordsAffected = expectedRecordsAffected;
    }

    public string CommandText { get; }
    public IReadOnlyDictionary<string, object?> Parameters { get; }

    /// <summary>
    /// Optional exact row-count expectation for safety-sensitive DML. When
    /// present, providers must roll back the containing transaction if the
    /// command affects a different number of rows.
    /// </summary>
    public int? ExpectedRecordsAffected { get; }
}
