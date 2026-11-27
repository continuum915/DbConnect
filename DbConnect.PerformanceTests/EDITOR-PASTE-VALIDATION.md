# Editor paste fixtures and validation

## Sizes and workload shapes

| Size | Purpose | Suggested frequency |
|---|---|---|
| 64 KiB (65,536 bytes) | Fast correctness, clipboard, binding, rendering and report smoke check | Every relevant editor change |
| 1 MiB | Normal substantial import; first scaling point | Each measured run |
| 16 MiB | Large import; expose CPU, allocation and dispatcher stalls | Each baseline |
| 64 MiB | Stress boundary; a supervised timeout/crash is useful evidence | Opt-in baseline on the reference machine |

Each size has `representative`, `many-small-batches` and `long-lines` profiles. The
first is the primary baseline. Many-small-batches varies GO frequency; long-lines
adds 4,096 characters per parent literal to expose layout/tokenization sensitivity.
The existing fixed-seed generator emits ASCII SQL, UTF-8 without BOM and CRLF;
MiB means 1,048,576 bytes. Inputs contain related-table INSERTs, escaped quotes,
multiline literals, real GO directives, and misleading tokens inside comments and
literals. They contain no real parameters. Parents/Children are assumed to exist:
these are editor inputs, not database provisioning scripts.

Three byte-exact 64 KiB examples and their manifest are versioned in
[Fixtures/EditorPaste](Fixtures/EditorPaste). Larger generated inputs live under
the repo's ignored `artifacts/performance/editor-inputs` directory. The 12-file
default matrix occupies about 243 MiB. Git preserves the small SQL samples byte for
byte; each manifest records SHA-256, exact bytes/characters, lines, inserts, GO
count, maximum line length, generator version, seed and configuration.

## Generate and verify the files

Run from the repository root in PowerShell:

```powershell
./build/performance/generate-editor-scripts.ps1
```

This builds Release once, generates the matrix and validates every file's size
and hash. Regeneration is deterministic, including the manifest; timings and
machine-specific fields are excluded from fixture manifests. To regenerate the
small versioned samples:

```powershell
./build/performance/generate-editor-scripts.ps1 -SizesKiB 64 -OutputDirectory DbConnect.PerformanceTests/Fixtures/EditorPaste
```

Custom example:

```powershell
./build/performance/generate-editor-scripts.ps1 -SizesKiB 64,1024 -Profiles representative,long-lines
```

## Automated correctness and measurements

```powershell
dotnet test DbConnect.Tests/DbConnect.Tests.csproj -c Release --filter 'FullyQualifiedName~EditorPastePerformanceTests|FullyQualifiedName~EditorScriptGenerationTests'
./build/performance/run-editor-paste.ps1 -Mode Smoke
./build/performance/run-editor-paste.ps1 -Mode Baseline
./build/performance/run-editor-paste.ps1 -Mode Variants
```

Smoke runs 64 KiB representative input with one excluded warm-up and one measured
sample. Baseline runs 1/16/64 MiB representative input; Variants runs those sizes
with the other two profiles. Both default to one excluded warm-up and five measured
samples per combination. Each sample gets a fresh supervised process. Runs are
sequential and enforce a 300-second deadline per child. Use `-Repetitions 1
-TimeoutSeconds 60` for a shorter coverage check, not a statistically meaningful
reference baseline. Specify `-OutputDirectory` to retain an identifiable report.
Builds occur before the matrix, never between samples; use `-SkipBuild` after an
explicit Release build. Run without a debugger and avoid concurrent benchmarks.

The primary case hosts the real QueryTabView at 1000x700 with production styles,
TextMate and Skia headless rendering. It warms a small document, starts from an
empty editor, verifies the clipboard round trip, then invokes normal editor Paste.
It validates the entire document, view-model text, caret and fixture hash after
operation timing. No database session or MainWindow/recovery timer is created.
The parent assigns isolated persistence before app initialization and removes it
after exit, including failed/timed-out children.

JSON and Markdown reports under `artifacts/performance` distinguish:

- Cold app startup and real-view warm-up, outside operation timing.
- Synchronous paste-command return and actual input completion.
- A subsequent completed layout/headless render opportunity, elapsed from input start.
- A bounded 300 ms deferred-work observation and final dispatcher drain.
- Process-wide allocations/GC and sampled whole-process memory, with separate diagnostic GC.
- Timestamped enqueue-to-dispatch delays at 20 ms cadence, sample count, maximum,
  missed intervals and raw delays. Only one callback may be pending; no catch-up
  bursts occur. Percentiles are omitted below 20 samples.

`Metrics.DurationMilliseconds` is the whole operation envelope, including the fixed
observation interval; compare the separate `Editor` input/render fields for paste
costs. Never equate a returned command, visual-lines validity, or a rendered frame
with completed background highlighting. Layout updates disclose apparent activity;
the tokenizer queue's completion remains unknown. Headless clipboard/render data
does not measure native Windows clipboard throughput, GPU presentation or input-to-display latency.

If clipboard support is unavailable the paste case fails explicitly. A separately
labeled direct-document comparison can be selected with:

```powershell
dotnet DbConnect.PerformanceTests/bin/Release/net10.0/DbConnect.PerformanceTests.dll --scenarios editor.insert --repetitions 1 --output artifacts/performance/editor-insert
```

Failures/timeouts return a nonzero exit and remain in reports with the last phase,
exit code and memory samples. Inspect them before rerunning; do not count them as
successful measurements. Compare only matching input hashes, profiles, generator
versions, settings and builds on the same machine/runtime. Five operation samples
support median/range reporting, not an operation-p95 claim. Allocation figures
include probe/frame/protocol overhead; background highlighting may extend beyond
the observation window.

## Interactive validation process

1. Build Release, launch outside the debugger in a separate terminal with an
   isolated `DBCONNECT_DATA_DIRECTORY`, then open a blank query tab with Ctrl+T.
   Example environment setup: `$env:DBCONNECT_DATA_DIRECTORY = "$PWD/artifacts/performance/manual-profile"`.
   Use an unconnected query tab for this editor-only task.
2. Start with the 64 KiB representative file. Prepare the native clipboard before
   observing paste: `Set-Clipboard -Value ([IO.File]::ReadAllText((Resolve-Path
   DbConnect.PerformanceTests/Fixtures/EditorPaste/representative-64KiB.sql)))`.
3. Focus the empty query editor and press Ctrl+V once. Observe first visible text,
   caret repaint and when menus respond again. Record any freeze, exception,
   unexpected prompt or partial insertion. Treat these as functional observations;
   use the supervised harness for timings.
4. Ctrl+A/Ctrl+C in the editor, then compare `Get-Clipboard -Raw` with the original
   file text in PowerShell. Require an exact match, including multiline literals
   and the final comment. Confirm the beginning/end are navigable and highlighting
   is present. Avoid executing the fixture as part of paste validation.
5. Repeat from a fresh empty tab for many-small-batches and long-lines, then 1,
   16 and 64 MiB representative inputs in that order. Record profile/hash/build,
   machine, outcome and any apparent continuing activity. Keep the window size
   fixed; close unrelated workloads. A slow 64 MiB case is evidence, not a reason
   to omit it from the record.
6. Close each tab/window and the isolated app. Editing/undo/navigation timing,
   integrated Run/recovery and retention measurements belong to subsequent tickets.

Restore the terminal's prior `DBCONNECT_DATA_DIRECTORY` setting when finished. This
task establishes measurements and correctness; optimization thresholds will follow
repeatable evidence from the reference machine.

## Incremental edit comparison

`build/performance/run-editor-edits.ps1` runs a paired full-refresh/incremental comparison through the real query editor. `Smoke` (default) uses 1 MiB with one warm-up and one measured repetition per mode; `Full` uses 1/16/64 MiB with one warm-up and five measured repetitions. Each repetition checks headless text input, undo, redo, replacement and deletion. All changes remain unconnected to a database session.

```powershell
./build/performance/run-editor-edits.ps1 -Mode Smoke
./build/performance/run-editor-edits.ps1 -Mode Full -OutputDirectory artifacts/performance/editor-edits
```

The runner alternates mode order in the same warmed window. `full=true` deliberately clears the behavior's tracker via reflection before the edit, forcing its production full-refresh fallback. It asserts whether the structure was reused, verifies exact document/view-model content, and compares fold ranges, applied folding sections and sampled matching offsets against precomputed full-analysis oracles outside timing. Repetition zero is discarded; raw records retain synchronous input time/allocation, render opportunity and dispatcher probes. Smoke produces 20 records; Full produces 180.

These are paired single-process diagnostics, separate from supervised fresh-child paste baselines. Expected documents/oracles remain alive during both modes; allocations are process-wide synchronous-window estimates, not retained or peak memory. Headless text input does not measure physical keyboard latency, connected completion or native display latency. The 60 ms observation and sparse probe samples do not establish completed highlighting or support percentile claims. The runner uses a unique temporary application-data directory and removes it after its headless session is disposed.

The query-text synchronization prototype extends the edit reports with synchronous-window GC deltas and a separate explicit consumer probe after each group's delete operation. Other edits validate the immutable model snapshot through readers instead of forcing a full string per key. `FirstConsumerReadMilliseconds`, `FirstConsumerAllocatedBytes` and `CachedConsumerReadMilliseconds` are null where no consumer is requested. Lazy synchronization defers full-string cost; callers that need the entire SQL still pay materialization once per revision, and the probe records that cost rather than counting it as eliminated.

### GC-attributed undo audit

Run ./build/performance/run-editor-undo-audit.ps1 -Mode Smoke (or -Mode Full) to compare committed c86b028 against the working prototype in fresh alternating headless processes. The runner builds both versions, validates every mutation and captures GC-only EventPipe traces plus separate weak-retention diagnostics. See build/performance/UndoAudit/README.md for interpretation and limits.

The TextMate allocation follow-up replaces the pinned adapter package with the licensed source project under `ThirdParty/AvaloniaEdit.TextMate`. Focused guards are in `DbConnect.Tests/TextMateMutationTests.cs`. The undo audit supplies a distinct initial editor string and checks that the model does not alias the retained expected fixture. Use combined edit/getter windows when evaluating latency; weak reuse does not guarantee a cache hit or prevent all GC stalls.