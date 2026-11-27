param(
    [ValidateSet('Smoke', 'Baseline', 'Variants')][string]$Mode = 'Smoke',
    [ValidateRange(0, 100)][int]$Repetitions = 0,
    [ValidateRange(1, 3600)][int]$TimeoutSeconds = 300,
    [string]$OutputDirectory = '',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
if (!$OutputDirectory) { $OutputDirectory = 'artifacts/performance/editor-' + $Mode.ToLowerInvariant() + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') }
if ($Repetitions -eq 0) { $Repetitions = if ($Mode -eq 'Smoke') { 1 } else { 5 } }
$scenario = switch ($Mode) { 'Smoke' { 'editor.smoke' }; 'Baseline' { 'editor.paste' }; 'Variants' { 'editor.variants' } }
Push-Location $repoRoot
try {
    if (!$SkipBuild) {
        dotnet build DbConnect.PerformanceTests/DbConnect.PerformanceTests.csproj -c Release --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw 'Performance harness build failed.' }
    }
    dotnet DbConnect.PerformanceTests/bin/Release/net10.0/DbConnect.PerformanceTests.dll --scenarios $scenario --repetitions $Repetitions --timeout-seconds $TimeoutSeconds --output $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw "The run recorded failed/timed-out cases. Inspect $OutputDirectory/report.json and report.md." }
}
finally { Pop-Location }
