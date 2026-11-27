namespace DbConnect.PerformanceTests;

public sealed record RunSettings(string ScenarioId, string Profile = "harness", long SizeBytes = 0);
public sealed record MemorySample(double ElapsedMilliseconds, long PrivateBytes, long WorkingSetBytes);
public sealed record PhaseTiming(string Phase, double ParentElapsedMilliseconds, double ChildElapsedMilliseconds);
public sealed record OperationMetrics(double DurationMilliseconds, long AllocatedBytes, int[] CollectionsByGeneration,
    long PostOperationHeapBytes, long ForcedGcDiagnosticHeapBytes);
public sealed record RunRecord(RunSettings Settings, int Repetition, bool Warmup, string Outcome, int? ExitCode,
    string LastCompletedPhase, List<MemorySample> MemorySamples, OperationMetrics? Metrics, string? Failure,
    double SupervisedDurationMilliseconds, MemorySample? IdleMemory = null, List<PhaseTiming>? Phases = null,
    ImportMetadata? Fixture = null, PreparationObservations? Observations = null, ExecutionObservations? Execution = null, RecoveryObservations? Recovery = null, EditorObservations? Editor = null, string? EditorDataDirectory = null, ExecutionStopObservations? Stop = null,
    SetupMemoryObservation[]? Setup = null, SetupMemoryObservation[]? RunMemory = null);
public sealed record ChildMessage(string Phase, OperationMetrics? Metrics = null, double ChildElapsedMilliseconds = 0,
    ImportMetadata? Fixture = null, PreparationObservations? Observations = null, ExecutionObservations? Execution = null, RecoveryObservations? Recovery = null, EditorObservations? Editor = null, ExecutionStopObservations? Stop = null,
    SetupMemoryObservation? Setup = null, SetupMemoryObservation? RunMemory = null);
