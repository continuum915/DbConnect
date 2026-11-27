[CmdletBinding()]
param(
    [string]$TestProject = (Join-Path $PSScriptRoot '../DbConnect.Tests/DbConnect.Tests.csproj'),
    [string]$Filter,
    [switch]$NoBuild,
    [switch]$NoRestore,
    [string[]]$TestArguments = @()
)

$ErrorActionPreference = 'Stop'
$testExitCode = 1
$testDataParent = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) 'rain-tests'))
$testRunDirectory = Join-Path $testDataParent ([guid]::NewGuid().ToString('N'))
$ownedMarker = '.dbconnect-test-run'
$markerVersion = 'rain-test-run-v1'
$savedEnvironment = @{}
$environmentKeys = @('DBCONNECT_TEST_RUN_DIRECTORY', 'DBCONNECT_DATA_DIRECTORY', 'DBCONNECT_PERSISTENCE_PROBE', 'DBCONNECT_PERSISTENCE_PROBE_DIRECTORY', 'DBCONNECT_RECOVERY_CONTENT_PROBE')

function Remove-OwnedTestRun {
    param([string]$Directory)
    $resolved = (Resolve-Path -LiteralPath $Directory).Path
    if ([IO.Path]::GetDirectoryName($resolved) -ne $testDataParent -or
        [IO.Path]::GetFileName($resolved) -ne [IO.Path]::GetFileName($testRunDirectory) -or
        ([IO.File]::GetAttributes($testDataParent) -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        [IO.File]::ReadAllText((Join-Path $resolved $ownedMarker)) -ne $markerVersion) {
        throw 'Refusing to remove an unowned test-run directory.'
    }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($resolved)
    while ($pending.Count -gt 0) {
        $entryDirectory = $pending.Pop()
        if (([IO.File]::GetAttributes($entryDirectory) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Refusing to clean a test directory containing a link.'
        }
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($entryDirectory)) {
            $attributes = [IO.File]::GetAttributes($entry)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Refusing to clean a test directory containing a link.'
            }
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) { $pending.Push($entry) }
        }
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

try {
    [void][IO.Directory]::CreateDirectory($testDataParent)
    if (([IO.File]::GetAttributes($testDataParent) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The test temporary parent must not be a link.'
    }
    [void][IO.Directory]::CreateDirectory($testRunDirectory)
    [IO.File]::WriteAllText((Join-Path $testRunDirectory $ownedMarker), $markerVersion)
    foreach ($key in $environmentKeys) {
        $savedEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        Remove-Item -LiteralPath "Env:$key" -ErrorAction SilentlyContinue
    }
    [Environment]::SetEnvironmentVariable('DBCONNECT_TEST_RUN_DIRECTORY', $testRunDirectory, 'Process')
    Write-Host "DbConnect test run: $testRunDirectory"
    $dotnetArguments = @('test', $TestProject)
    if ($Filter) { $dotnetArguments += @('--filter', $Filter) }
    if ($NoBuild) { $dotnetArguments += '--no-build' }
    if ($NoRestore) { $dotnetArguments += '--no-restore' }
    $dotnetArguments += $TestArguments
    & dotnet @dotnetArguments
    $testExitCode = $LASTEXITCODE
}
catch {
    Write-Warning "Test runner failed: $($_.Exception.GetType().Name)."
    $testExitCode = 1
}
finally {
    foreach ($key in $savedEnvironment.Keys) {
        if ($null -eq $savedEnvironment[$key]) {
            Remove-Item -LiteralPath "Env:$key" -ErrorAction SilentlyContinue
        }
        else {
            [Environment]::SetEnvironmentVariable($key, $savedEnvironment[$key], 'Process')
        }
    }
    if (Test-Path -LiteralPath $testRunDirectory) {
        try { Remove-OwnedTestRun -Directory $testRunDirectory }
        catch { Write-Warning "Test cleanup failed for $testRunDirectory. Temporary data was retained." }
    }
}
exit $testExitCode
