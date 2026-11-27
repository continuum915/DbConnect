using System.Threading;
using System.Threading.Tasks;

namespace DbConnect.Services;

/// <summary>Shared routine defaults, executing saves through the owning provider's query role.</summary>
internal class DatabaseRoutineProvider(IDatabaseQueryProvider queries) : IDatabaseRoutineProvider
{
    public async Task SaveProcedureAsync(string connectionString, string definition, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await queries.ExecuteNonQueryAsync(connectionString, definition);
    }
}
