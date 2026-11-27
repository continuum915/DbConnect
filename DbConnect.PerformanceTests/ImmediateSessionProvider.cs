using System.Reflection;
using DbConnect.Services;

namespace DbConnect.PerformanceTests;

// Only metadata and session creation are supported: an unexpected provider call fails closed.
public class ImmediateSessionProvider : DispatchProxy
{
    private ImmediateDbSession _session = null!;
    internal static IDatabaseProvider Create(ImmediateDbSession session)
    {
        var provider = Create<IDatabaseProvider, ImmediateSessionProvider>();
        ((ImmediateSessionProvider)provider)._session = session;
        return provider;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "get_ProviderName" => _session.Provider.ProviderName,
        "get_ProviderIcon" => _session.Provider.ProviderIcon,
        "get_Capabilities" => _session.Provider.Capabilities,
        "GetActiveDatabaseLabel" => null,
        "OpenSessionAsync" => Task.FromResult<IDbSession>(_session),
        _ => throw new NotSupportedException($"Integrated measurement forbids provider call {method?.Name}.")
    };
}
