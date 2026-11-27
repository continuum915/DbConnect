using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Reads a complete, same-provider definition of a table for Script as CREATE.</summary>
public interface ITableScriptSource
{
    /// <exception cref="TableDefinitionUnavailableException">Part of the definition cannot be read.</exception>
    Task<TableDefinitionSnapshot> GetTableSnapshotAsync(string connectionString, DatabaseObjectIdentity table,
        CancellationToken cancellationToken = default);
}
