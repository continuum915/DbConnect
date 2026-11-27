# Supervised performance harness

## Execution modes and CI

Build once from the repository root, then invoke the built DLL for every sample:

```powershell
dotnet build DbConnect.Tests/DbConnect.Tests.csproj -c Release -p:UsedAvaloniaProducts=
dotnet test DbConnect.Tests/DbConnect.Tests.csproj -c Release --no-build --filter FullyQualifiedName~PerformanceModeTests
dotnet DbConnect.PerformanceTests/bin/Release/net10.0/DbConnect.PerformanceTests.dll --mode smoke --output artifacts/performance/smoke
dotnet DbConnect.PerformanceTests/bin/Release/net10.0/DbConnect.PerformanceTests.dll --mode baseline --output artifacts/performance/baseline
dotnet DbConnect.PerformanceTests/bin/Release/net10.0/DbConnect.PerformanceTests.dll --mode extended --output artifacts/performance/extended --timeout-seconds 300 --repetitions 5 --sampling-ms 20
dotnet DbConnect.PerformanceTests/bin/Release/net10.0/DbConnect.PerformanceTests.dll --compare artifacts/performance/baseline/report.json artifacts/performance/candidate/report.json --output artifacts/performance/comparison
```

Compare baseline with another baseline run saved to `candidate`, using the same
controls and machine where possible; extended and baseline are different selections.
The comparison still matches individual compatible workloads and labels missing groups.

| Mode | Selection | Default measured repetitions |
| --- | --- | ---: |
| smoke | No-op/idle, 64 KiB execution preparation, parameter substitution and real editor paste; fixed small error/cancellation correctness scripts | 1 |
| baseline | Representative 1/16/64 MiB: all five preparation operations, paste, insert, middle typing, direct fake execution and isolated recovery persistence | 5 |
| extended | Baseline plus all preparation profiles, actual-parameter detection/substitution, editor paste variants, start/end typing, navigation, selection/undo/redo, integrated Run/recovery, error/cancellation and post-close release | 5 |

Every workload gets one excluded warm-up plus the requested number of measured
fresh children, sequentially. `--repetitions` overrides either default. No timing
assertions or performance budgets are applied. Child correctness failures, crashes
and timeouts still fail the command and remain in reports. Required workloads are
never silently skipped. `coverage.json` and the main JSON/Markdown report list
selected settings, expected/recorded children, outcomes and concrete failure or
blocked reasons. Native input/display latency, completed background highlighting
and real-server interruption are explicitly unsupported measurements with reasons.
Headless clipboard paste uses the Avalonia test clipboard, not the OS clipboard;
render opportunities do not prove visible frames or completed highlighting.

`--mode` and `--scenarios` are mutually exclusive. Custom comma-separated scenario
selection and the legacy no-argument no-op/idle selection remain available, with
five measured repetitions by default. `--output` sets the report directory;
`--timeout-seconds` (default 300) bounds each child, including setup and validation;
`--sampling-ms` (default 20) controls requested memory sampling. There is no
whole-run timeout locally: baseline/extended can take hours and substantial RAM.
Generated SQL stays in child memory; reports do not include large SQL fixtures.
Raw JSON can grow with long-running samples, while `coverage.json` is a compact
metadata/coverage artifact. Partial observations and memory samples remain available.

To split a large matrix into separately saved batches, filter by scenario ID,
profile and size with `--scenario-id`, `--profile` and `--size-mib`. For example,
this runs only 16 MiB long-line parameter detection:

```powershell
dotnet DbConnect.PerformanceTests/bin/Release/net10.0/DbConnect.PerformanceTests.dll --scenarios variants.all --scenario-id prepare.parameters --profile long-lines --size-mib 16 --repetitions 1 --timeout-seconds 30 --output artifacts/performance/long-lines-parameters-16
```

Filters can also be used with `--mode`. They narrow the selected workloads, so
the report describes only that batch; run and preserve the other batches separately
for full coverage. A filter combination matching no workload is rejected. Each
batch still writes its report at the end, so an interrupted batch has no final
report; choose batch sizes and per-child timeouts accordingly. One measured
repetition is useful for coverage, not a stable distribution.

The GitHub `Performance measurements` workflow runs smoke for relevant pull requests.
Manual dispatch selects any mode, repetitions and per-child timeout. It builds the
test project and its harness dependency once, uses `--no-build` for focused checks,
then executes the DLL without rebuilding between samples. An always-run upload
publishes only `report.json`, `report.md` and `coverage.json`, with 14-day retention.
The job has a 360-minute safety limit; external job termination can prevent final
report writing, so reduce repetitions or select individual scenarios for longer
investigations. Baseline/extended remain opt-in and outside ordinary unit tests.
Hosted-runner timings are informational pending machine variability evidence.

This opt-in executable measures harness overhead, script preparation, isolated recovery and real-editor paste. Editor scenarios initialize the real Avalonia query view using isolated profile storage; processing scenarios retain their service-only setup. It makes no live database connections. Ordinary xUnit execution only runs
small correctness checks, including the deliberately failing child scenarios.

From the repository root, build once and execute without rebuilding between samples:

```powershell
dotnet build DbConnect.PerformanceTests/DbConnect.PerformanceTests.csproj -c Release
dotnet DbConnect.PerformanceTests/bin/Release/net10.0/DbConnect.PerformanceTests.dll
```

The default runs `harness.noop` and `harness.idle`, sequentially, with one excluded
warm-up and five measured repetitions each. Every repetition, including warm-up,
uses a fresh child process. Debug builds and attached debuggers are rejected.
Idle waits 100 ms; no-op measures the empty operation envelope. These runs disclose
instrumentation overhead rather than establish production performance claims.

```powershell
dotnet DbConnect.PerformanceTests/bin/Release/net10.0/DbConnect.PerformanceTests.dll --output artifacts/performance/failure-check --scenarios harness.noop,harness.crash,harness.hang --timeout-seconds 2 --repetitions 1 --sampling-ms 10
dotnet test DbConnect.Tests/DbConnect.Tests.csproj -c Release --filter FullyQualifiedName~PerformanceHarnessTests
```

Options are value pairs. `--output` selects a directory containing `report.json`
and `report.md`; the default is a unique timestamped directory under ignored
`artifacts/performance`. Reusing a directory replaces its reports. The default
timeout is 300 seconds per child and memory sampling cadence is 20 ms. The parent
returns 0 only when every run, including warm-up, completed, 1 for recorded run
failures, and 2 for invalid invocation or report-writing errors. The failure-check
command deliberately returns 1. No timing thresholds are enforced.

JSON contains raw memory samples, idle memory at the pre-operation handshake,
operation metrics, phase timestamps, exit codes, failure details, all repetitions,
and median/minimum/maximum summaries excluding warm-ups and unsuccessful runs.
Allocation counters are process-wide, GC counts cover generations 0–2, and heap
bytes before and after diagnostic forced GC remain separate. No GC is forced
inside an operation. There is no fixture for these scenarios; future fixtures must
be created before timing and kept alive consistently through heap diagnostics.
Parent phase timestamps include startup and protocol overhead; child timestamps
are monotonic since the first harness phase. Peaks cover the entire supervised
process lifetime, including startup, validation, serialization, and diagnostic GC.
Actual memory sample timestamps disclose scheduler jitter; short spikes can be missed.

Reports capture runtime/OS/architecture, processor count, machine name, Release
configuration, package versions from the built dependency manifest, and build-time
repository commit and dirty state embedded in the assembly. Invocation from another
directory cannot change that identity. An unavailable build Git identity is
explicitly labeled. Five samples do not support reliable operation p95 claims.

Representative preparation (opt-in):

```powershell
dotnet DbConnect.PerformanceTests/bin/Release/net10.0/DbConnect.PerformanceTests.dll --scenarios prepare.all --repetitions 1 --timeout-seconds 60 --output artifacts/performance/preparation
```

`prepare.all` expands `prepare.parameters`, `prepare.split`, `prepare.spans`,
`prepare.execution`, and `prepare.highlighter`, each at 1, 16, and 64 MiB.
Individual IDs can also be selected with `--scenarios`. Defaults remain the two
harness scenarios; the normal repetition default remains five plus one warm-up.
`prepare.execution` measures parameter detection followed by splitting and span
parsing, the preparation combination used by Run/execution; it submits no commands.
`prepare.highlighter` constructs the real RunningStatementHighlighter including
its parsing; it does not measure editor rendering or TextMate syntax highlighting.
The query tab and source text are prepared before timing, with no app startup,
profile persistence, or database connection.

Generator `representative-v1` uses seed 1729 and an explicit 32-bit LCG
(multiplier 1664525, increment 1013904223). Each related parent/child pair has
escaped and multiline string literals, apparent parameters and separators in
comments/literals, and a real GO after every 32 pairs plus a final GO. A bounded
multiline comment pads to the exact requested ASCII/UTF-8 size, without a BOM;
line endings are CRLF. Tables are assumed to exist (Parents and Children); this
is synthetic preparation input, not a database provisioning script.

Reports include per-run generation duration, version, seed, profile, SHA-256,
encoded bytes, UTF-16 characters, lines (including trailing empty line), actual GO
directives, INSERT count, tables and maximum line length. Generator ranges and
parent/child markers are constructed independently of production parsing and
remain in memory alongside the source and operation output through diagnostic GC.
Validation runs after counters stop and checks coverage/order, returned text,
parameter absence and observable highlighter spans. SQL Server parser units,
GO batches, and executable INSERTs are distinct; comment-only units and batch
splitting differences are explicitly reported without modifying production SQL
semantics. Validation can add parsing work to whole-lifetime sampled peaks.
Large SQL fixtures are generated only in child memory and never written to disk.

Workload variants (opt-in):

```powershell
dotnet DbConnect.PerformanceTests/bin/Release/net10.0/DbConnect.PerformanceTests.dll --scenarios variants.all --repetitions 1 --timeout-seconds 20 --output artifacts/performance/variants
```

`variants.all` selects 51 combinations at 1, 16, and 64 MiB: all five preparation
scenarios for `many-small-batches`, `few-large-batches`, and `long-lines`, plus
`parameter.detect` and `parameter.substitute` for `actual-parameters`. The two
parameter scenario IDs can also be selected individually. Actual-parameter input
is rejected by ordinary preparation scenarios to keep these costs distinct.

The `variants-v1` generator uses the same seed/LCG and independent INSERT markers
as the representative generator. Configuration is serialized with fixture metadata:

| Profile | Parent/child pairs per GO | Extra characters per parent literal | Actual parameter |
| --- | ---: | ---: | --- |
| representative | 32 | 0 | none |
| many-small-batches | 1 | 0 | none |
| few-large-batches | 512 | 0 | none |
| long-lines | 32 | 4096 | none |
| actual-parameters | 32 | 0 | @value |

Every profile ends with a GO and valid comment padding; when a periodic GO occurs
on the last pair the final GO is an additional empty batch. Apparent GO lines in
multiline literals/comments are excluded from directive metadata. Long-line inputs
require at least 8192 bytes; other profiles require at least 1024. The representative
SQL/hash stays unchanged. Compare matching scenario IDs, profile, version, seed,
configuration, size and SHA-256; sizes alone do not identify equivalent workloads.

Actual parameters occur only as `+ @value` in the parent Name expression, while
lexical decoys use different names. Substitution times the existing
`SubstituteWithOffsets` API with string value `D'Angelo`; it excludes detection.
Outside timing, validation constructs expected escaped SQL and every source offset
from independent INSERT markers. The entire fixture/oracle remains alive through
measurement and diagnostic GC. These are synthetic preparation costs, not database
throughput. Named-placeholder substitution's behavior for identical tokens inside
comments/literals is not exercised by this profile; no production semantics change.

Direct batch execution (opt-in):

```powershell
dotnet DbConnect.PerformanceTests/bin/Release/net10.0/DbConnect.PerformanceTests.dll --scenarios execute.direct --repetitions 1 --timeout-seconds 45 --output artifacts/performance/direct
```

`execute.direct` runs the production QueryBatchExecutor at 1, 16 and 64 MiB with
SQL Server dialect capabilities and a deterministic immediate IDbSession. No
provider execution methods or live connections are used. Each submitted command
returns one small affected-row result, including comment-only padding. This
measures orchestration, not database throughput or SQL Server affected-row semantics.
Defaults remain unchanged; the supervisor can kill each child on its deadline.

Operation wall time and process-wide allocations include splitting, span parsing,
executor metadata/result creation and fake async enumeration plus a streaming
64-bit FNV-style hash (UTF-16 characters followed by command length). First
submission time starts immediately before the executor call. The fake stores only
counters/hash/timing, never submitted SQL or results. Its checksum work is included
in orchestration time, so these measurements are not pure executor CPU cost.

After timing, returned SQL and source offsets are checked against independent
generator INSERT ranges and comment prefixes, in order, followed by final padding.
Command counters/hash are compared with these verified results. Missing, changed,
reordered or duplicated work fails validation. SQL Server semicolon splitting,
GO batch counts and comment-only submissions are explicitly disclosed separately.

Result retention is a diagnostic paired full-GC heap difference: production results
are held for the first reading, released for the second. The input fixture and its
generator oracle, fake, scenario, observations and harness remain alive for both.
The delta includes SQL retained by production results, statement metadata and result
containers; it excludes stable fake/fixture overhead. This noisy heap estimate is
reported signed without clamping, alongside both absolute readings. It is outside
operation counters; lifetime memory peaks include validation and both collections.
The existing forced-GC heap metric is taken before results are released.

Isolated recovery persistence (opt-in):

```powershell
dotnet DbConnect.PerformanceTests/bin/Release/net10.0/DbConnect.PerformanceTests.dll --scenarios recovery.persist --repetitions 1 --timeout-seconds 30 --output artifacts/performance/recovery
```

Runs representative 1, 16 and 64 MiB query text plus a small second tab through
SessionRecoveryService with a fresh temporary directory. No app initialization,
real recovery reads, session restoration or live connections occur. The existing
interface combines indented JSON serialization, directory checking and writing;
independent serialization/write attribution is unavailable. Writes include OS cache
and do not guarantee durable storage. Reports record filesystem, drive type,
available space and path alongside the standard metrics. Snapshot creation,
complete persisted-content validation and recursive cleanup are outside timing.
The fixture and snapshot remain alive through diagnostic GC. Exceptions fail the
child and remain in supervisor reports. A forcibly killed child may leave its
uniquely named DbConnect-performance-recovery-* temporary directory; its path is in the
fixture-ready report context once initialization completes. This measures direct
persistence, not the app's debounce or integrated recovery scheduling.

## Real editor paste and SQL fixtures

See [EDITOR-PASTE-VALIDATION.md](EDITOR-PASTE-VALIDATION.md) for the generated 64 KiB/1/16/64 MiB inputs, generation/hash checks, supervised smoke/baseline commands, phase interpretation, and interactive validation process.

## Real editor typing

Run the three positions at 1, 16 and 64 MiB with one warm-up and five measured fresh children per combination:

```powershell
dotnet DbConnect.PerformanceTests/bin/Release/net10.0/DbConnect.PerformanceTests.dll --scenarios editor.typing.start,editor.typing.middle,editor.typing.end --output artifacts/performance/editor-typing
```

Each child loads the representative fixture into the real query editor, places the caret at offset 0, half its character count, or its end, and pauses 300 ms before the ready handshake. It then uses headless text input to insert `x`. The reported synchronous return and input completion are separate from the following render opportunity and bounded 300 ms observation. Content, model binding, caret, and advancing document version are validated afterward against the fixture plus the insertion. Reports include fixture identity, input offset, raw dispatcher samples and missed intervals. The fixture remains alive through diagnostic GC; operation allocations include observation and probe overhead. These measurements do not establish completed highlighting, native keyboard/display latency, or operation p95.

## Real editor selection, replacement, undo and redo

```powershell
dotnet build DbConnect.PerformanceTests/DbConnect.PerformanceTests.csproj -c Release --no-restore -p:UsedAvaloniaProducts=
dotnet DbConnect.PerformanceTests/bin/Release/net10.0/DbConnect.PerformanceTests.dll --scenarios editor.selection-undo --output artifacts/performance/selection-08b
```

This opt-in scenario uses the deterministic representative ASCII/CRLF fixture at
1, 16 and 64 MiB. Each fresh child uses the real 1000x700 QueryTabView with
production TextMate highlighting and Skia headless rendering. It warms the UI
with `SELECT 1;`, loads the fixture, clears setup undo history, focuses the editor,
places the caret one quarter through the document, and pauses 300 ms before the
ready handshake. The default policy discards one fresh-child warm-up and retains
five measured children per size; `--repetitions 1` is a shorter diagnostic run.

The four operations select the middle half of the document using
`TextEditor.Select`, replace it with `SELECT 2;` using `Window.KeyTextInput`, then
invoke `TextEditor.Undo` and `TextEditor.Redo`. Selection is a programmatic range;
undo/redo use editor command APIs rather than simulated shortcut routing. This
does not measure physical mouse/keyboard or native display latency.

Each operation records its own synchronous return, background dispatcher drain,
layout/render opportunity, bounded 300 ms deferred observation, allocation and
GC deltas, layout events, dispatcher delays and missed intervals. Duration and
allocation totals sum these four envelopes, excluding checks and report messages
between them. Exact document/model text, selection, caret and version changes are
validated outside each envelope. The selection must leave the version unchanged;
replacement, undo and redo must each advance it. Hashes and state observations
are retained for each validated step, including partial runs that later fail or
time out. Standard supervision retains every outcome and lifetime memory samples.

Validation deliberately reads the model text after each step, materializing its
lazy snapshot; this consumer cadence affects subsequent undo/redo behavior.
The fixture and replacement oracle remain alive through the final diagnostic GC.
No forced collection occurs inside an operation. A frame, idle observation or
valid visual lines do not establish completed background highlighting. GC counts
can accompany slow samples but do not prove pause attribution; a GC trace is
needed to establish a GC-driven stall. No stall-free or reliable operation-p95
claim is made from these small samples. `UsedAvaloniaProducts=` suppresses the
build-time telemetry target when its user-profile log is sandbox-inaccessible.

## Real editor navigation and scrolling

```powershell
dotnet build DbConnect.PerformanceTests/DbConnect.PerformanceTests.csproj -c Release --no-restore -p:UsedAvaloniaProducts=
dotnet DbConnect.PerformanceTests/bin/Release/net10.0/DbConnect.PerformanceTests.dll --scenarios editor.navigation-scroll --output artifacts/performance/navigation-08c
```

The opt-in scenario expands to representative 1, 16 and 64 MiB documents. The
standard supervisor runs one discarded fresh-child warm-up and five measured
fresh children per size, retaining every outcome and raw sample. Each child
uses the real 1000x700 QueryTabView, production highlighting, isolated app data
and Skia headless rendering. Setup renders `SELECT 1;`, pauses 50 ms, loads the
fixture, clears setup undo history, focuses the caret at offset zero, scrolls to
the top and pauses 300 ms. Fixture generation and all setup are excluded.

The sequence is: navigate to the start of the line containing the middle
character; scroll three individual viewports down, then three up; navigate to
EOF; scroll one viewport up, then one down. Navigation sets CaretOffset and calls
ScrollToLine. Scrolling sets the owning ScrollViewer.Offset, keeping the caret
unchanged. It is programmatic scrolling, not a mouse-wheel or shortcut replay.
The pinned TextEditor.ScrollToVerticalOffset is a no-op and is not used. Targets,
word wrap, actual initial viewport and per-step viewport/extent sizes, offsets
and visible-line ranges are recorded in device-independent pixels (DIP).

One scroll requests exactly one observed pre-operation viewport height and clamps
to the observed extent minus viewport. Small fixtures can reach an edge; no-op
and clamped steps are retained and labeled by requested/target/actual offsets.
Navigation must leave its target line visible; scroll-only steps preserve the
caret. Every step independently measures synchronous API return, layout/frame
opportunity, bounded 300 ms observation and dispatcher delays/missed intervals,
plus allocation/GC deltas and a heap sample before validation. The shared envelope
is also used by selection/undo measurements, with unchanged correctness checks.

Exact editor/model text is checked using streaming snapshots outside timing,
without an explicit full-text getter between steps. Document version must remain
unchanged and selection empty. Initial settings/viewport are sent at fixture-ready,
with zero timings/probe samples until the first completed step; subsequent
validated steps are published individually so failures/timeouts retain partial
observations. Fixture/oracle strings remain alive through final diagnostic GC.
No forced collection occurs inside an operation. Report summaries separate
settings, fixture, window dimensions, word wrap and observed viewport dimensions;
warm-ups and unsuccessful runs are excluded from successful statistics.

Frames and visible lines describe headless layout/render opportunities; neither
native FPS, physical input-to-display latency nor completed background highlighting
is established. Small sample counts do not justify reliable operation p95; raw
probe samples and counts remain available. GC counts do not establish stall
attribution. The scenario creates no live session or MainWindow/recovery scheduler.

## Editor release after close

```powershell
dotnet build DbConnect.PerformanceTests/DbConnect.PerformanceTests.csproj -c Release --no-restore -m:1 -p:UsedAvaloniaProducts=
dotnet DbConnect.PerformanceTests/bin/Release/net10.0/DbConnect.PerformanceTests.dll --scenarios editor.release --output artifacts/performance/editor-release
```

The standard fresh-child supervisor uses the representative 1, 16 and 64 MiB
fixtures, one warm-up and five measured children per size. Each child opens the
real 1000x700 headless query view with TextMate, edits one character in the
middle and undoes it, leaving redo history. The fixture and a string-backed
validation oracle remain alive through both diagnostics. Content and redo
availability are checked before an open full-GC diagnostic. The scenario then
closes the window, detaches view and model and releases the session, recording
close timing, a bounded dispatcher observation and queue samples. After the UI
creation scope returns and another dispatcher drain and 300 ms delay, a separate
full-GC diagnostic records weak-reference survival for editor, view, document,
model snapshot and model. Partial runs retain completed phases. Full collections
are outside operation timing.

The open redo history is expected retention. Weak-reference survival is evidence
about specific targets only, not a root analysis or proof of complete release.
Framework queues and caches may hold targets; managed heap is process-wide and
private bytes and working set include native/runtime effects and need not fall
after GC. Fixture/oracle retention is deliberate. This isolated window is not a
MainWindow tab-close or recovery-session measurement; headless frames do not
measure physical display latency or prove highlighting has completed.

Integrated Run and normal recovery scheduling (opt-in):

```powershell
dotnet DbConnect.PerformanceTests/bin/Release/net10.0/DbConnect.PerformanceTests.dll --scenarios execute.integrated,editor.paste-recovery --repetitions 1 --timeout-seconds 60 --output artifacts/performance/integrated-run
```

Each ID expands to representative 1, 16, and 64 MiB in fresh supervised children,
including a discarded warm-up at every size. Both verify the parent-assigned empty
profile before Avalonia initialization. The existing provider interface supplies a
fail-closed deterministic session, without a production injection seam or live I/O.
`execute.integrated` invokes `MainWindowViewModel.ExecuteQueryCommand`: parameter
detection, running-statement highlighter preparation, worker executor orchestration,
and normal view-model result presentation. It uses a real QueryTabView; its tab has
no history store, so history serialization is excluded. Normal recovery scheduling
is active, including during long commands. Compact fake-session counts, character
counts and rolling hashes are checked against presented results and the generator
oracle after operation counters stop. No extra submitted-SQL list is retained.
Integrated execution evidence is in `Editor.Integrated`, separate from direct
executor measurements; retention fields in that evidence are unmeasured (zero).

`Editor.Integrated.Stages` records optional wall-clock intervals relative to command
start for detection, substitution (when used), highlighter preparation, batch
execution, result assignment, error navigation, history registration, dispatcher
queue drain, and explicit layout/frame capture. Stage entry/exit dispatcher flags
do not imply continuous dispatcher occupancy: batch execution awaits worker work
and statement highlighting. Queue drain includes pending UI work; the separate
300 ms observation window is not presentation time. Older records without stages
remain readable and are reported as unmeasured. The `DbConnect.QueryRun` ActivitySource
is inactive without a listener and emits stage names only, never SQL or values.

Run establishes its busy state and cancellation token before resolving the source.
`resolve-source`, `detect-parameters`, `substitute-parameters` (when present), and
`prepare-highlighter` activities execute on workers using captured editor text and
selection offsets. The highlighter is attached on the dispatcher with precomputed
spans. Cancellation checks are shared by the source, parameter, and statement scans,
including long comments, quoted text, names, ordinals, and unchanged output ranges.
Dialogs and result presentation remain on the dispatcher. Integrated tests verify
the worker stage boundaries as well as command output.

`Dispatcher.Samples` adds due/enqueue/completion times, queue and cadence delays,
GC-pause deltas while queued, and GC-pause deltas since the previous sample. For
integrated Run these times share the command/stage origin; the initial synchronization
sample can have a negative timestamp. Other scenarios use probe construction as
their origin. `MaximumCadenceDelayMilliseconds` includes producer lateness and avoids
hiding process-wide GC pauses that prevent the producer from enqueueing a probe.
Keep the existing maximum queue delay for compatibility, but gate both maxima.
Stage GC-pause and allocated-byte deltas are process-wide, include concurrent work,
and must not be summed as exclusive attribution. These counters correlate stalls;
they do not provide individual GC event start/end timestamps. Older reports leave
these optional fields unmeasured.

Integrated setup emits `ChildMessage.Setup` boundary samples before `fixture-ready`.
The parent retains them in `RunRecord.Setup`, including partial stages from killed
children, and the Markdown report includes a setup memory table. Samples contain
child-lifetime timestamps, activity/parent IDs, working set, private bytes, managed
heap, cumulative allocation/GC-pause counters, and dispatcher access. The optional
`DbConnect.EditorSetup` activity source covers view/window construction, editor assignment,
text publication, structure/folding updates, explicit layout, recovery input read,
production recovery serialization/write, and harness recovery readback verification.
The listener stops before Run starts. Boundary counters are process-wide, include
concurrent/background work and tracing overhead, and cannot be summed across nested
stages. No forced GC or SQL content is recorded. Headless initialization is sampled
without creating/accessing the dispatcher prematurely. The external parent guard
still supplies the independent sampled peak and enforces the memory budget.

Recovery persistence now writes the legacy indented JSON schema as UTF-8 with
16,384-character string segments and progressive async flushes, then publishes the
closed temporary file to the existing snapshot name. The `recovery-serialize` setup
interval therefore includes interleaved encoding and stream writes; its duration is
not directly comparable to the earlier CPU-only full-string serialization interval.
Normal recovery scheduling is unchanged. Setup readback verification now uses a
bounded buffered UTF-8 reader and checks every decoded SQL character against the
existing fixture string, without deserializing another full SQL value or calling
editor.Text to create an expected-text copy. It validates the fixed recovery schema,
one-tab count, current PID, and scalar metadata using independent checks. Metadata
strings are capped at 4096 characters for these synthetic profiles. Production
recovery reading is unchanged.

After `fixture-ready`, `command-starting` marks the command invocation. Optional
`ChildMessage.RunMemory` boundaries are immediately flushed around Run stages and
the coarse `DbConnect.QueryMemory` parsing/materialization/execution stages. The parent
retains them in `RunRecord.RunMemory` and reports them separately from setup. The
post-ready checkpoint and memory boundary timestamps share the child-lifetime clock;
stage timing/dispatcher samples still use their documented command origin. The
external wrapper records the last Run stage for killed children. No per-statement
memory events or SQL values are emitted. Process-wide counters include diagnostic
overhead and concurrent work; no forced GC is used to lower the pre-Run baseline.

For unchanged submitted SQL, integrated Run reuses the statement spans already
prepared for highlighting. Selection coordinates are rebased to the submitted SQL;
parameter substitution retains a fresh parse because lengths/boundaries can change.
The coarse memory interval is named `prepare-execution-spans` for both paths.

Batch execution now materializes each statement string immediately before submission
instead of creating an up-front list of all statement strings. The single-statement
selection path still submits its original text verbatim. Executed SQL remains retained
by result/statement records. The `materialize-statements` interval is absent from new
captures; string creation is included in `execute-statements`, so compare that interval
with both earlier intervals when assessing allocation. Early error/cancellation stops
do not copy SQL for statements that were never reached.

`Editor.Integrated.HighlightUpdates` counts non-null running-statement highlight
changes during the command. Run coalesces highlights at 100 ms intervals for batches
over 128 statements; smaller batches keep per-statement updates. The result selector
retains all results while virtualizing its dropdown containers. Compare dispatcher
delay and highlight counts alongside command duration when evaluating large runs.

`editor.paste-recovery` uses real clipboard paste with the real MainWindowViewModel
workspace subscriptions and unchanged two-second recovery debounce. Setup recovery
is allowed to settle and its snapshot is removed before timing. Synchronous paste
return and document-change completion are separate timestamps. The operation
envelope includes the normal bounded 300 ms render observation/dispatcher drain
plus an additional 2500 ms recovery observation. The debounce is observation, never
paste duration. No forced write or flush is performed. Snapshot JSON, current PID,
one-tab count, title, active title and exact editor text are verified after counters
stop. Missing or incomplete snapshots fail the run. Dispatcher samples cover only
the command/render window; background highlighting completion is unknown. The
parent records all failures/timeouts and removes each isolated directory after exit.
These are headless local measurements, not native clipboard/display or database
throughput. Setup, validation and diagnostic GC remain outside operation counters.

Execution stop correctness (opt-in, fixed small script):

```powershell
dotnet DbConnect.PerformanceTests/bin/Release/net10.0/DbConnect.PerformanceTests.dll --scenarios execute.error,execute.cancel --repetitions 1 --timeout-seconds 15 --output artifacts/performance/execution-stop
# Deliberate unsuccessful cleanup checks; expected parent exit code 1:
dotnet DbConnect.PerformanceTests/bin/Release/net10.0/DbConnect.PerformanceTests.dll --scenarios harness.stop-cleanup-hang,harness.stop-cleanup-failure --repetitions 1 --timeout-seconds 5 --output artifacts/performance/execution-stop-cleanup
dotnet test DbConnect.Tests/DbConnect.Tests.csproj -c Release --filter FullyQualifiedName~ExecutionStop
```

These IDs use `profile=harness`, `size=0` with the fixed three-command script
`SELECT 101; SELECT 202; SELECT 303;`. They do not expand to large input sizes.
They are correctness/orchestration scenarios, separate from immediate-fake
throughput; defaults remain unchanged. `execute.error` injects structured error
`DBCONNECT_FAKE_ERROR` at command two. `execute.cancel` waits until command two has
signaled that it is blocked, then cancels the execution-options token. The fake
returns a structured canceled result through the existing executor path. Monotonic
cancellation latency covers request to executor return, excluding cleanup and
validation. No sleep-based trigger, driver call, profile access or connection is
involved. This does not prove real-provider/server interruption or the driver's
`IDbSession.CancelAsync` behavior and does not change batch semantics.

After timing, literal command/result checks verify only commands one and two were
submitted, the first succeeded, the second is the selected error/canceled result,
and no active command remains after disposal. The tiny fake retains only this
small script's submitted commands; it is never used for large-SQL throughput.
Owned cancellation, gate release, pending execution and disposal are handled on
both success and failure. `Stop` in JSON and the separate Markdown correctness
section contain expected/observed stop status, command/result counts, error code,
cancellation flag/latency and cleanup evidence. A completed child means these
checks passed, not that every statement in the script succeeded.

The two `harness.stop-cleanup-*` reliability diagnostics use the cancellation path
and deliberately hang or throw in disposal after releasing the owned gate. The
parent preserves the `cleanup-started` phase, exit/failure information, phase
timestamps and memory samples; failed/timed-out records have no successful metrics.
Operation counters include executor work, phase instrumentation and cleanup;
validation, report serialization and diagnostic forced GC remain outside timing.

Compare saved reports (offline; no children or database connections):

```powershell
dotnet DbConnect.PerformanceTests/bin/Release/net10.0/DbConnect.PerformanceTests.dll --compare artifacts/performance/baseline/report.json artifacts/performance/candidate/report.json --output artifacts/performance/comparison
```

`--compare baseline.json candidate.json [--output directory]` writes
`comparison.md` and `comparison.json`. Output defaults to a unique directory under
`artifacts/performance`. Valid comparisons return 0 even when groups are incompatible
or inputs contain failed runs; differences are informational. Invalid arguments,
unsupported schema, unreadable/malformed JSON, or output that would overwrite an
input return 2. This offline command also works in Debug builds and does not change
source reports or rerun measurements. Schema 1 measurement reports are supported.

Matching uses the full per-run settings, fixture metadata excluding generation
time (including generator/version/seed/hash/configuration/size), parent timeout,
sampling cadence, measurement policy and warm-up count for the run settings.
Editor configuration includes input method, window geometry, offsets, selection
setup/replacement, transformers, navigation settings, ordered operation names/input
methods and dispatcher cadence. Recorded recovery attribution/storage/write semantics
and stop-scenario configuration also participate. Output paths, repetition numbers,
measured sample count and temporary profile/storage paths are not workload identity.
Missing fixture identity or editor configuration cannot establish compatibility,
even when missing on both sides. Partial failed records can therefore be listed
separately as incompatible; their failures and raw data remain visible.

Each compatible group shows median/minimum/maximum across successful measured runs,
with explicit ms/bytes/collection units, signed candidate-minus-baseline absolute
and percentage differences, valid/missing metric counts and raw run indices.
Percentage is `(candidate median - baseline median) / baseline median * 100`;
a zero baseline has no percentage, and a missing baseline/candidate has no delta.
Warm-ups and failed/timed-out/crashed runs never enter metric statistics. Every
outcome, exit code, last phase, failure and memory-sample count remains in Markdown;
JSON embeds both complete original reports including all raw samples and metadata.
No reliable operation p95 is claimed from five samples. Dispatcher p50/p95 are
recomputed from raw delays only when at least 20 valid nonnegative samples match
the declared count; per-run counts are disclosed. Distributions of those per-run
percentiles do not pool independent probe windows. Integrated result-retention
fields are unmeasured in schema 1 and are not compared.

Runtime, OS, machine, architecture, processor count, build configuration, package
versions and any additional recorded hardware metadata changes are explicitly
flagged. Workload-compatible deltas remain available with a warning that they are
not a controlled comparison. Commit and dirty state remain visible as source
context. Schema 1's machine/architecture/processor count cannot prove equal CPU
model, RAM or system load when those details were never recorded. No performance
pass/fail budgets are introduced.

New reports also persist `EditorProtocols` keyed by scenario ID. This records the
protocol version and intended deferred-observation target separately from observed
elapsed time, plus additional recovery/release windows where applicable. Shared
constants drive the actual waits and recorded values. These settings must match.
Older editor reports without this protocol are explicitly incompatible: their
intended observation window cannot be inferred from elapsed measurements. Legacy
non-editor reports can still match their recorded workload/settings/policy.

### Inline MERGE workload proxy

`execute.merge-proxy` expands to integrated Run at 8, 16 and 64 MiB using the separate `merge-import-proxy` profile. The original stress fixture remains available unchanged. This proxy fixes counts at 4,281 GO-terminated statements, 94 tables, 3,291 Product statements, 4,279 inline VALUES MERGEs and two DELETEs, with no standalone INSERT statements. Row counts (1–32), ordering, column shapes and string widths are synthetic; byte padding lives in VALUES data.

The wrapper measures 16 then 64 MiB and retains the existing deadlines, 2 GiB sampled process memory budget and 100 ms queue/cadence gates. Complete output is checked against generator ranges and compact submission evidence; verified MERGE/DELETE counts are persisted alongside INSERT counts. The fake database measures app overhead and does not predict SQL Server time.
