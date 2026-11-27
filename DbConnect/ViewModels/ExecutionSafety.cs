using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Services;

namespace DbConnect.ViewModels;

/// <summary>What the user is asked to confirm before SQL that changes data runs.</summary>
public sealed record DataChangeConfirmation(string ConnectionName, string EnvironmentTier, string Statement);

/// <summary>
/// Asks before running SQL that may change data on a connection whose environment tier is listed in
/// the Execution preferences (Production by default). Read-only SQL runs without asking.
/// </summary>
public sealed class ExecutionSafety
{
    /// <summary>Shows the confirmation; true to run. Without it, SQL that needs confirming does not run.</summary>
    public Func<DataChangeConfirmation, Task<bool>>? ConfirmDataChange { get; set; }

    /// <summary>
    /// Why a read-only connection refuses <paramref name="sql"/>, or null when it may run. Anything not known to
    /// be read-only is refused, as DataGrip's read-only connections do.
    /// </summary>
    public static async Task<string?> ReadOnlyRefusalAsync(DatabaseSession session, string sql, CancellationToken cancellationToken)
    {
        if (session.Connection.Options?.IsReadOnly != true) return null;
        var dialect = session.Capabilities.BatchDialect;
        var change = await Task.Run(() => DataChangeClassifier.FirstDataChange(sql, dialect, cancellationToken), cancellationToken);
        if (change == null) return null;
        var preview = change.Length > 200 ? change[..200] + "…" : change;
        return $"{session.Name} is a read-only connection, so DbConnect did not run SQL that may change data: {preview}";
    }

    /// <summary>True when <paramref name="sql"/> may run on <paramref name="session"/>.</summary>
    public async Task<bool> ConfirmRunAsync(DatabaseSession session, string sql, CancellationToken cancellationToken)
    {
        var tier = session.Connection.EnvironmentTier;
        if (string.IsNullOrEmpty(tier) ||
            !AppPreferences.Execution.ConfirmDataChangesOnTiers.Contains(tier, StringComparer.OrdinalIgnoreCase))
            return true;

        var dialect = session.Capabilities.BatchDialect;
        var change = await Task.Run(() => DataChangeClassifier.FirstDataChange(sql, dialect, cancellationToken), cancellationToken);
        if (change == null) return true;
        return ConfirmDataChange != null &&
               await ConfirmDataChange(new DataChangeConfirmation(session.Name, tier, change));
    }
}
