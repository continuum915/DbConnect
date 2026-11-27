# Undo stress comparison

This opt-in Windows diagnostic builds identical audit source against committed `c86b028` and the current worktree. It never edits production code to select a mode.

```powershell
./build/performance/run-editor-undo-audit.ps1 -Mode Smoke
./build/performance/run-editor-undo-audit.ps1 -Mode Full
```

Full alternates version order in three blocks for each of two 64 MiB workloads. Each fresh child runs one warm-up and five measured cycles. A cycle contains type, undo, redo, replace and delete, plus three cleanup undos. `every-change` requests the complete SQL string after all eight changes; `measured-only` requests it after the five primary operations. Both versions use the same headless input, frame/60 ms observation, full document/folding/matching validation and process-wide allocation/GC counters. Cleanup operations are recorded and must be included when comparing total mutation cost.

GC-only EventPipe traces record suspension windows; their absolute Windows QPC timestamps are aligned to edit and consumer phases. Getter timing excludes string verification, weak tracking and content/folding validation. Combined edit-plus-getter cost matters: the prototype may move an existing allocation/GC pause to a consumer. Tracing introduces overhead; these diagnostics are not native input/display latency or operation-p95 evidence.

No forced GC occurs during replay. After timing/tracing stops, diagnostic collections count obsolete model snapshots, source wrappers and materialized strings by weak identity, excluding fixtures and the current revision. Expected scripts/oracles and the VM intentionally remain alive. View/control weak references and open/closed managed-heap totals are reported, but do not establish complete UI release: existing callbacks/dispatcher work can retain controls in either version. No managed-root heap graph is captured.

Smoke runs 4 children × 16 changes; Full runs 12 children × 48 changes. Each child is limited to four minutes and failure exits nonzero. Successful children have raw messages, trace analysis and `.nettrace`/`.etlx` artifacts. Per-child profile directories and the clean temporary reference checkout are removed. The output builds/traces remain in ignored repo artifacts; a 64 MiB run can use several GiB of memory.

The initial editor input is a distinct string from the retained expected fixture. An explicit initial read before tracing verifies that the model string does not alias that fixture; the ready message records this check. This prevents fixture identity from keeping the weakly cached initial model string alive artificially. Both comparison binaries are rebuilt from this same audit source.
