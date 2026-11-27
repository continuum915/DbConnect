using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

public interface ITableDefinitionProvider
{
    Task<TableRebuildDefinition> GetTableDefinitionAsync(string connectionString, string tableName);
}
