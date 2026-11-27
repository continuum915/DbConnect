using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Reads the structured design of an existing table for the table designer.</summary>
public interface ITableDesignSource
{
    /// <exception cref="TableDefinitionUnavailableException">Part of the design cannot be read.</exception>
    Task<TableDesign> GetTableDesignAsync(string connectionString, DatabaseObjectIdentity table,
        CancellationToken cancellationToken = default);
}
