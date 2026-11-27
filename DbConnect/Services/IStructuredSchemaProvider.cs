using System.Collections.Generic;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Catalog operations that never parse an object name as a dotted path.</summary>
public interface IStructuredSchemaProvider
{
    Task<List<ColumnDefinition>> GetColumnsAsync(string connectionString, DatabaseObjectIdentity relation);
    Task<List<SchemaNode>> GetChildrenAsync(string connectionString, DatabaseObjectIdentity parent, SchemaNodeType kind);
    Task<Dictionary<string, EnumColumnInfo>> GetEnumColumnsAsync(string connectionString, DatabaseObjectIdentity relation) =>
        Task.FromResult(new Dictionary<string, EnumColumnInfo>());
}
