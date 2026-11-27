using System;
using System.Threading;

namespace DbConnect.Services;

/// <summary>Flows cancellation through the legacy schema-role methods during a catalog read only.</summary>
internal sealed class MetadataReadContext : IDisposable
{
    private static readonly AsyncLocal<CancellationToken> Current = new();
    private readonly CancellationToken _previous;
    public static CancellationToken Token => Current.Value;
    public MetadataReadContext(CancellationToken token) { _previous = Current.Value; Current.Value = token; }
    public void Dispose() => Current.Value = _previous;
}
