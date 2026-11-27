namespace DbConnect.PerformanceTests;

/// <summary>Intended observation windows, kept separate from measured elapsed times.</summary>
public sealed record EditorProtocolSettings(int Version, int DeferredObservationTargetMilliseconds,
    int? AdditionalRecoveryObservationMilliseconds, int? CloseObservationMilliseconds)
{
    public static Dictionary<string, EditorProtocolSettings> Capture(IEnumerable<string> scenarios) => scenarios
        .Where(EditorPasteChild.IsEditor).Distinct(StringComparer.Ordinal).ToDictionary(id => id, id => new EditorProtocolSettings(
            1, EditorDeferredWork.ObservationTargetMilliseconds,
            id == IntegratedRunScenario.RecoveryId ? IntegratedRunScenario.RecoveryObservationMilliseconds : null,
            id == EditorReleaseScenario.Id ? EditorReleaseScenario.CloseObservationMilliseconds : null), StringComparer.Ordinal);
}
