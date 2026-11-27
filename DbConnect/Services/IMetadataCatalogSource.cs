using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using DbConnect.Models;

namespace DbConnect.Services;

/// <summary>Focused metadata read role. Implementations return a whole scope or throw.</summary>
public interface IMetadataCatalogSource
{
    Task<ImmutableArray<MetadataRecord>> ReadAsync(MetadataScope scope, CancellationToken cancellationToken);
}
