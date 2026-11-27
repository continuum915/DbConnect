param(
    [ValidateSet('Smoke', 'Full')][string]$Mode = 'Smoke',
    [string]$OutputDirectory = '',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
if (!$OutputDirectory) { $OutputDirectory = 'artifacts/performance/editor-edits-' + (Get-Date -Format 'yyyyMMdd-HHmmss') }
Push-Location $repoRoot
try {
    if (!$SkipBuild) {
        dotnet build DbConnect.PerformanceTests/DbConnect.PerformanceTests.csproj -c Release --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw 'Performance harness build failed.' }
    }
    $runnerArguments = @('DbConnect.PerformanceTests/bin/Release/net10.0/DbConnect.PerformanceTests.dll', '--editor-edits', $OutputDirectory)
    if ($Mode -eq 'Smoke') { $runnerArguments += '--smoke' }
    dotnet @runnerArguments
    if ($LASTEXITCODE -ne 0) { throw 'Editor edit validation failed.' }
}
finally { Pop-Location }
