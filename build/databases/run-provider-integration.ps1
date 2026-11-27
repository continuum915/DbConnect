[CmdletBinding()]
param(
    [string]$ComposeFile = (Join-Path $PSScriptRoot 'compose.yaml'),
    [string]$TestProject = (Join-Path $PSScriptRoot '..\..\DbConnect.Tests\DbConnect.Tests.csproj'),
    [string]$ResultsDirectory = (Join-Path ([System.IO.Path]::GetTempPath()) 'rain-provider-conformance'),
    [int]$StartupTimeoutSeconds = 240,
    [ValidateSet('All', 'PostgreSQL', 'SQLServer', 'MySQL')]
    [string]$Provider = 'All',
    [ValidateSet('docker', 'podman')]
    [string]$ContainerRuntime = 'podman',
    [switch]$KeepContainers
)

$ErrorActionPreference = 'Stop'
$composeArgs = @('compose', '-f', (Resolve-Path -LiteralPath $ComposeFile).Path)
$startupFailed = $false
$testExitCode = 0
$logPath = Join-Path $ResultsDirectory 'database-container.log'
$providerServices = @{
    PostgreSQL = 'postgres'
    SQLServer = 'sqlserver'
    MySQL = 'mysql'
}
$providerEnvironment = @{
    PostgreSQL = @{
        Key = 'DBCONNECT_CONFORMANCE_POSTGRESQL_CONNECTION_STRING'
        Value = 'Host=localhost;Port=5432;Database=rain_ecommerce;Username=rain;Password=rain_dev_password'
    }
    SQLServer = @{
        Key = 'DBCONNECT_CONFORMANCE_SQLSERVER_CONNECTION_STRING'
        Value = 'Server=tcp:127.0.0.1,1433;Database=rain_ecommerce;User Id=sa;Password=DbConnect_dev_password123;TrustServerCertificate=True'
    }
    MySQL = @{
        Key = 'DBCONNECT_CONFORMANCE_MYSQL_CONNECTION_STRING'
        Value = 'Server=localhost;Port=3306;Database=rain_ecommerce;User Id=rain;Password=rain_dev_password;SslMode=None;AllowPublicKeyRetrieval=True'
    }
}

function Invoke-Compose {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & $ContainerRuntime @composeArgs @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$ContainerRuntime compose $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Get-ServiceHealth {
    param([Parameter(Mandatory)][string]$Service)

    # -a: without it a crashed (exited) container is not listed and is misreported as missing.
    $containerLine = (& $ContainerRuntime @composeArgs ps -a -q $Service 2>$null | Select-Object -First 1)
    $containerId = if ($null -eq $containerLine) { '' } else { ([string]$containerLine).Trim() }
    if ([string]::IsNullOrWhiteSpace($containerId)) {
        return 'missing'
    }

    $stateJson = (& $ContainerRuntime inspect --format '{{json .State}}' $containerId 2>$null | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($stateJson)) {
        return 'missing'
    }

    $state = $stateJson | ConvertFrom-Json
    if ($state.Status -ne 'running') {
        return [string]$state.Status
    }

    if ($null -ne $state.Health) {
        return [string]$state.Health.Status
    }

    return 'running'
}

try {
    New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null

    & $ContainerRuntime compose version | Out-Host
    if ($LASTEXITCODE -ne 0) {
        $startupFailed = $true
        throw "$ContainerRuntime Compose is unavailable."
    }

    $startupFailed = $true
    $services = if ($Provider -eq 'All') {
        @('postgres', 'sqlserver', 'mysql')
    }
    else {
        @($providerServices[$Provider])
    }
    Invoke-Compose -Arguments (@('up', '-d', '--build') + $services)
    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $ready = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        $health = @{}
        foreach ($service in $services) {
            $health[$service] = Get-ServiceHealth -Service $service
        }

        if ($health.Values -contains 'unhealthy' -or $health.Values -contains 'missing' -or
            $health.Values -contains 'exited' -or $health.Values -contains 'dead') {
            $startupFailed = $true
            throw "A database container failed health checks: $($health | Out-String)"
        }

        if (($health.Values | Where-Object { $_ -eq 'healthy' }).Count -eq $services.Count) {
            $ready = $true
            break
        }

        Start-Sleep -Seconds 3
    }

    if (-not $ready) {
        $startupFailed = $true
        throw "Database containers did not become healthy within $StartupTimeoutSeconds seconds."
    }

    $startupFailed = $false

    # Keep credentials in process environment only; the script never prints the
    # connection strings or passes them as command-line arguments. Clear any
    # inherited lane variables so -Provider selects exactly one external engine.
    foreach ($entry in $providerEnvironment.Values) {
        Remove-Item -LiteralPath "Env:$($entry.Key)" -ErrorAction SilentlyContinue
    }

    $selectedProviders = if ($Provider -eq 'All') {
        @('PostgreSQL', 'SQLServer', 'MySQL')
    }
    else {
        @($Provider)
    }
    foreach ($providerName in $selectedProviders) {
        $entry = $providerEnvironment[$providerName]
        Set-Item -LiteralPath "Env:$($entry.Key)" -Value $entry.Value
    }
    $env:DBCONNECT_CSV_REQUIRED_PROVIDERS = $selectedProviders -join ','
    if ($selectedProviders -contains 'MySQL') {
        $env:DBCONNECT_CONFORMANCE_MYSQL_ADMIN_CONNECTION_STRING = 'Server=localhost;Port=3306;Database=rain_ecommerce;User Id=root;Password=rain_root_password;SslMode=None;AllowPublicKeyRetrieval=True'
    }

    & dotnet test $TestProject --no-build --filter 'Category=Integration' `
        --logger "trx;LogFileName=rain-provider-conformance.trx" `
        --results-directory $ResultsDirectory
    $testExitCode = $LASTEXITCODE
    if ($testExitCode -ne 0) {
        throw "Provider conformance tests failed with exit code $testExitCode."
    }
}
catch {
    $ErrorActionPreference = 'Continue'
    Write-Error $_
    if ($testExitCode -eq 0) {
        $testExitCode = if ($startupFailed) { 20 } else { 1 }
    }
}
finally {
    try {
        & $ContainerRuntime @composeArgs logs --no-color *>&1 | Set-Content -LiteralPath $logPath
    }
    catch {
        Write-Warning "Could not capture database container logs: $($_.Exception.Message)"
    }

    if (-not $KeepContainers) {
        try {
            & $ContainerRuntime @composeArgs down --volumes --remove-orphans
        }
        catch {
            Write-Warning "Could not clean up database containers: $($_.Exception.Message)"
            if ($testExitCode -eq 0 -and -not $startupFailed) { $testExitCode = 1 }
        }
    }
}

if ($startupFailed) {
    Write-Error "Provider integration lane did not start. Database startup/health failure is distinct from test failure (exit code 20)."
}

exit $testExitCode
