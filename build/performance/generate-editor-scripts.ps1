param(
    [string]$OutputDirectory = 'artifacts/performance/editor-inputs',
    [int[]]$SizesKiB = @(64, 1024, 16384, 65536),
    [string[]]$Profiles = @('representative', 'many-small-batches', 'long-lines'),
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
Push-Location $repoRoot
try {
    if (!$SkipBuild) {
        dotnet build DbConnect.PerformanceTests/DbConnect.PerformanceTests.csproj -c Release --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw 'Performance harness build failed.' }
    }
    dotnet DbConnect.PerformanceTests/bin/Release/net10.0/DbConnect.PerformanceTests.dll --generate-scripts $OutputDirectory --sizes-kib ($SizesKiB -join ',') --profiles ($Profiles -join ',')
    if ($LASTEXITCODE -ne 0) { throw 'Fixture generation failed.' }
    $manifestPath = Join-Path $OutputDirectory 'manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    foreach ($entry in $manifest.Files) {
        $file = Get-Item -LiteralPath (Join-Path $OutputDirectory $entry.FileName)
        if ($file.Length -ne $entry.EncodedBytes -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $entry.Sha256) {
            throw "Fixture size/hash verification failed: $($entry.FileName)"
        }
    }
    Write-Output "Verified $($manifest.Files.Count) SQL fixtures in $OutputDirectory"
}
finally { Pop-Location }
