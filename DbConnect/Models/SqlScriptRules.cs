namespace DbConnect.Models;

/// <summary>
/// How an engine's clients split a generated script into what they send: after a batch separator line such as SQL
/// Server's GO, or at a client delimiter that <c>DELIMITER</c> must change around a body holding statements of its own,
/// as MySQL's clients need. Table scripts and dumps terminate every statement through one instance per engine.
/// </summary>
public sealed record SqlScriptRules(string? BatchSeparator = null, bool DelimitCompoundBodies = false)
{
    public static SqlScriptRules Standard { get; } = new();

    /// <summary>
    /// <paramref name="sql"/> as a terminated statement. A <paramref name="compound"/> body (a routine or trigger) is
    /// wrapped in <c>DELIMITER $$</c> where the clients need it. Where batches are separated, the separator follows on
    /// its own line; <paramref name="asWritten"/> leaves the text without an added ';', for engines that keep a
    /// module's batch as its definition.
    /// </summary>
    public string Terminate(string sql, bool compound = false, bool asWritten = false, string newLine = "\n")
    {
        sql = sql.Trim();
        if (BatchSeparator is { } separator)
            return (asWritten ? sql : sql.TrimEnd(';') + ";") + newLine + separator;
        if (compound && DelimitCompoundBodies)
            return "DELIMITER $$" + newLine + sql.TrimEnd(';') + "$$" + newLine + "DELIMITER ;";
        return sql.EndsWith(';') ? sql : sql + ";";
    }
}
