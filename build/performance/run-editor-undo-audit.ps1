param(
    [ValidateSet('Smoke', 'Full')][string]$Mode = 'Smoke',
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repoRoot ('artifacts/performance/undo-audit-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts/performance'))
$reference = Join-Path $artifactRoot ('undo-reference-' + [Guid]::NewGuid().ToString('N'))
$created = $false
$blocks = if ($Mode -eq 'Full') { 3 } else { 1 }
$repetitions = if ($Mode -eq 'Full') { 5 } else { 1 }
Push-Location $repoRoot
try {
    git worktree add --detach $reference c86b028
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create the reference checkout.' }
    $created = $true
    $baseline = Join-Path $output 'baseline'
    $prototype = Join-Path $output 'prototype'
    dotnet build build/performance/UndoAudit/UndoAudit.csproj -c Release "-p:DbConnectRoot=$reference" "-p:AuditOutputDirectory=$baseline" --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Reference audit build failed.' }
    dotnet build build/performance/UndoAudit/UndoAudit.csproj -c Release "-p:DbConnectRoot=$repoRoot" "-p:AuditOutputDirectory=$prototype" --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Prototype audit build failed.' }
    foreach ($cadence in @('every-change', 'measured-only')) {
        dotnet (Join-Path $prototype 'UndoAudit.dll') --parent (Join-Path $output $cadence) (Join-Path $baseline 'UndoAudit.dll') (Join-Path $prototype 'UndoAudit.dll') $blocks $repetitions $cadence
        if ($LASTEXITCODE -ne 0) { throw "Undo audit failed: $cadence" }
    }
    Write-Output $output
}
finally {
    if ($created -and (Test-Path -LiteralPath $reference)) {
        $resolved = (Resolve-Path -LiteralPath $reference).Path
        if ($resolved -ne [IO.Path]::GetFullPath($reference) -or !$resolved.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe reference cleanup path.' }
        if ((git -C $resolved rev-parse HEAD) -ne 'c86b02863340344b8f825750c221e799a2c01377' -or (git -C $resolved status --porcelain)) { throw 'Reference checkout changed; left for inspection.' }
        git worktree remove $resolved
        if ($LASTEXITCODE -ne 0) { throw 'Reference checkout cleanup failed.' }
    }
    Pop-Location
}
