namespace DbConnect.PerformanceTests;

public sealed record DispatcherSample(double EnqueuedMilliseconds, double CompletedMilliseconds,
    double DelayMilliseconds, double GcPauseMilliseconds, double DueMilliseconds,
    double CadenceDelayMilliseconds, double GcPauseSincePreviousSampleMilliseconds);

public sealed record DispatcherObservations(int CadenceMilliseconds, int SampleCount, long MissedIntervals,
    double MaximumDelayMilliseconds, double? P50Milliseconds, double? P95Milliseconds, double[] QueueDelaysMilliseconds,
    DispatcherSample[]? Samples = null, double? MaximumCadenceDelayMilliseconds = null);

public sealed record EditorObservations(string InputMethod, int DocumentCharacters, string DocumentSha256,
    string IsolatedDirectory, double ColdStartupMilliseconds, double WarmupMilliseconds,
    double SynchronousInputMilliseconds, double InputCompletedMilliseconds,
    double LayoutRenderOpportunityMilliseconds, double ObservationMilliseconds, int LayoutUpdatesDuringObservation,
    bool VisualLinesValidAtEnd, bool HighlightingCompletionClaimed, string[] LineTransformers,
    DispatcherObservations Dispatcher, int WindowWidth = 1000, int WindowHeight = 700, int? InputOffset = null,
    EditorOperationObservations[]? Operations = null, int? InitialSelectionStart = null,
    int? InitialSelectionLength = null, string? Replacement = null, EditorNavigationSettings? Navigation = null,
    EditorReleaseObservations? Release = null, IntegratedRunObservations? Integrated = null);

public sealed record EditorOperationObservations(string Name, string InputMethod,
    double SynchronousMilliseconds, double LayoutRenderOpportunityMilliseconds,
    double ObservationMilliseconds, double DurationMilliseconds, long AllocatedBytes,
    int[] CollectionsByGeneration, int LayoutUpdatesDuringObservation,
    DispatcherObservations Dispatcher, int DocumentCharacters, string DocumentSha256,
    int SelectionStart, int SelectionLength, int CaretOffset, int VersionAge, long PostOperationHeapBytes,
    EditorViewportObservations? Viewport = null);
