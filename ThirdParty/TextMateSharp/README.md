# Patched TextMateSharp engine

This directory contains the MIT-licensed TextMateSharp engine from commit
`4532112f6ee96c8d7847ee66a79719dfe58e9f43`, the exact source commit recorded by
NuGet TextMateSharp 2.0.3. DbConnect uses this source project in place of that package.
The grammar resources and Onigwrap remain their existing NuGet dependencies.

`upstream-sources.json` pins the original URLs and SHA-256 hashes for all 98
imported files, including the original project, license and upstream signing key.
`upstream.patch` contains the complete changes to imported files. The 96 other
imported files are byte-identical to the pinned source. Unmodified upstream source
retains its original formatting and namespace conventions. New maintenance files
are local additions. The local .editorconfig marks these imported sources as externally maintained for formatter/analyzer purposes (generated_code), so the solution-wide CI format command does not rewrite upstream formatting; local behavior changes are reviewed through the patch. No compiled engine binaries are checked in.

The engine change is confined to `Model/AbstractLineList.cs`: its private list is
concrete, and a protected `UpdateLineRange` operation allocates inserted entries
once, removes/inserts ranges in a bounded number of list calls, and publishes the
adapter snapshot under the existing line-list lock. Existing single-line methods,
interfaces and grammar algorithms are preserved. Unaffected ModelLine objects are
retained. The callback does not acquire the tokenizer's queue lock; adapter
invalidation occurs after the line-list lock is released, preserving lock order.
This does not promise a transaction across arbitrary separately issued reader
calls or fix unrelated tokenizer concurrency behavior.

The local SDK project targets DbConnect's net10.0, disables packaging, pins Onigwrap
1.0.10, and uses the upstream-distributed signing key. It preserves assembly name,
version 2.0.3.0 and public key token `2f2c40f8a499edb3`, so the unchanged signed
TextMateSharp.Grammars package binds to the same identity. Informational version
is `2.0.3-rain-bulk-lines`. The resolved dependency graph must contain
TextMateSharp as a project, with a single runtime DLL. The MIT license copies to
`licenses/TextMateSharp.txt` in build/publish outputs.

Refresh procedure:

1. Select an explicit upstream commit and fetch its engine sources, root license
   and signing key. Verify every imported file against the source manifest.
2. Reassess/reapply the source patch and local SDK project settings. Preserve the
   strong-name identity required by the resolved grammar package, or deliberately
   update and verify the complete dependency set.
3. Regenerate the manifest and patch from pristine upstream files. Validate the
   patch with `git apply --check --reverse --directory=ThirdParty/TextMateSharp
   ThirdParty/TextMateSharp/upstream.patch` from the repository root.
4. Run the headless mutation/token/selection and small-edit allocation checks,
   complete app suite, Release build/publish, and supervised 1/16/64 MiB selection
   benchmark. Check the runtime dependency graph and output identity/license.
5. Remove this fork when an upstream package supports the required bulk behavior
   and passes those checks. Do not overwrite the local patch as a routine upgrade.
