# Large-selection scaling diagnostic (Windows)

This is an opt-in diagnosis tool, separate from the ticket-08b baseline runner.
It leaves application behavior unchanged and retains synthetic-input traces and
raw measurements under an explicit output directory.

```powershell
dotnet restore build/performance/SelectionAudit/SelectionAudit.csproj
dotnet build build/performance/SelectionAudit/SelectionAudit.csproj -c Release --no-restore -p:UsedAvaloniaProducts=
dotnet build/performance/SelectionAudit/bin/Release/net10.0/SelectionAudit.dll --scale artifacts/performance/selection-scaling/reproduction
dotnet build/performance/SelectionAudit/bin/Release/net10.0/SelectionAudit.dll --layers artifacts/performance/selection-scaling/layers 4
dotnet build/performance/SelectionAudit/bin/Release/net10.0/SelectionAudit.dll --layers artifacts/performance/selection-scaling/model 8 'bare,model'
dotnet build/performance/SelectionAudit/bin/Release/net10.0/SelectionAudit.dll --analyze artifacts/performance/selection-scaling/model
```

The scaling command uses the existing fresh-child supervisor and real
`editor.selection-undo` workflow at 1, 2 and 4 MiB, with exact per-operation state
validation. It returns 1 if the fourfold-size replacement ratio exceeds six, as
an exploratory signal of superlinear work. This heuristic is deliberately not a
unit-test threshold, release gate, complexity proof or reliable latency statistic.
A non-completed child returns 2. Raw runs remain in `scaling.json`.

The layer command runs one fresh, isolated child per selected layer, with a
60-second child deadline. Each gets identical deterministic representative SQL,
a 1000x700 Skia headless window, a `SELECT 1;` render/50 ms warm-up, then the
fixture, cleared setup undo history, middle-half selection and 300 ms pause.
Layers are:

- `bare`: standalone AvaloniaEdit editor.
- `model`: the same editor plus the production `TextEditorModel`, with no grammar,
  TMModel or tokenizer worker. This isolates adapter line/snapshot maintenance.
- `textmate`: standalone editor plus the production TextMate installation and SQL grammar.
- `full`: real `QueryTabView` and its view model, with normal behaviors/highlighting.

Replacement uses headless `KeyTextInput("SELECT 2;")`; undo/redo use editor command
APIs. Each synchronous call has Stopwatch/QPC, process-wide allocation and GC
counters. Exact editor/model text, newer document version, selection and caret
are checked after counters stop. A frame and 60 ms pause separate operations;
these differ from the baseline runner's 300 ms observation and do not establish
completed background highlighting. Direct internal child invocation requires a
parent-assigned absolute `DBCONNECT_DATA_DIRECTORY`. The parent removes its GUID
profile directory on exit; it saves messages and stderr even on failure.

Layer profiling starts after the ready handshake and before mutations. It
collects sampled stacks and CLR GC/suspension events via EventPipe, with rundown
for managed names. Analysis clips Windows QPC windows to each synchronous call.
It filters operation-thread stacks by the actual diagnostic `Step` ancestor,
separates all-thread counts, and retains inclusive/leaf counts. Inclusive frame
presence is not a CPU percentage; sleeping workers are excluded from the
operation-thread subset. Suspensions are reported by reason: `SuspendOther`
is not labeled GC. Profiling adds overhead, including suspension overhead.

`*-raw.json`, `*.nettrace`, `*.etlx`, `*-analysis.json`, stderr and environment
metadata remain in the output directory. Files ending `-cpu.json` in early
captures are superseded exploratory summaries; use `-analysis.json`. No forced GC
occurs during these diagnostic mutations. Fixture and expected strings remain
alive. This is not a native latency, completed-tokenization, p95, retention or
performance-regression guarantee. No live database sessions are opened.

If restore hits the observed sandbox Windows TLS restriction, all dependencies
may already be cached; the recorded local-only fallback is:

```powershell
dotnet restore build/performance/SelectionAudit/SelectionAudit.csproj --source "$env:USERPROFILE/.nuget/packages" -p:NuGetAudit=false
```

