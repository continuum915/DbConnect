using System.Collections.Generic;
using System.Linq;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Uses primary keys for stable ordering when available, otherwise all catalog columns.</summary>
public static class TableMetadataOrder
{
    public static (List<string> Keys, List<string> Order) FromSnapshot(MetadataSnapshot snapshot)
    {
        var keys = snapshot.Records.Where(column => column.IsPrimaryKey).Select(column => column.Identity.Name).ToList();
        return (keys, keys.Count > 0 ? keys : snapshot.Records.Select(column => column.Identity.Name).ToList());
    }
}
