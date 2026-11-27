using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>
/// Lists per-table statistics (rows, sizes, engine, times, collation, comment) for the Objects tab. Providers that
/// cannot supply them do not implement it, and the Objects tab shows "—".
/// </summary>
public interface ITableStatisticsSource
{
    Task<IReadOnlyList<TableStatistics>> GetTableStatisticsAsync(string connectionString, CancellationToken cancellationToken = default);
}
