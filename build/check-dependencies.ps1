[CmdletBinding()]
param(
    [string] $Solution = "DbConnect.slnx",
    [string] $ExceptionsPath = "build/dependency-exceptions.json",
    [string] $ReportPath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path ([IO.Path]::GetTempPath()) "rain-dotnet-vulnerability-report.json"
}

$reportDirectory = Split-Path -Parent $ReportPath
if (-not [string]::IsNullOrWhiteSpace($reportDirectory)) {
    New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
}

$stderrPath = Join-Path ([IO.Path]::GetTempPath()) ("rain-dotnet-vulnerability-{0}.err" -f [Guid]::NewGuid())

try {
    & dotnet list $Solution package --vulnerable --include-transitive --format json --output-version 1 > $ReportPath 2> $stderrPath
    $dotnetExitCode = $LASTEXITCODE

    if (-not (Test-Path -LiteralPath $ReportPath)) {
        throw "dotnet list package did not produce a vulnerability report. Exit code: $dotnetExitCode"
    }

    $json = Get-Content -LiteralPath $ReportPath -Raw
    if ([string]::IsNullOrWhiteSpace($json)) {
        throw "dotnet list package produced an empty vulnerability report. Exit code: $dotnetExitCode"
    }

    try {
        $report = $json | ConvertFrom-Json
    }
    catch {
        $stderr = if (Test-Path -LiteralPath $stderrPath) { Get-Content -LiteralPath $stderrPath -Raw } else { "" }
        throw "Unable to parse dotnet vulnerability report. Exit code: $dotnetExitCode. $stderr"
    }

    if ($report.PSObject.Properties.Name.Contains("problems")) {
        $problems = @($report.problems | ForEach-Object {
                "[{0}] {1}" -f ([string]$_.level), ([string]$_.text)
            })
        if ($problems.Count -gt 0) {
            throw "dotnet list package reported a problem. Exit code: $dotnetExitCode. $($problems -join '; ')"
        }
    }

    if (-not $report.PSObject.Properties.Name.Contains("projects")) {
        throw "dotnet list package produced a report without projects. Exit code: $dotnetExitCode"
    }

    $findings = @(
        foreach ($project in @($report.projects)) {
            if (-not $project.PSObject.Properties.Name.Contains("frameworks")) {
                continue
            }

            foreach ($framework in @($project.frameworks)) {
                foreach ($packageListName in @("topLevelPackages", "transitivePackages")) {
                    if (-not $framework.PSObject.Properties.Name.Contains($packageListName)) {
                        continue
                    }

                    foreach ($package in @($framework.$packageListName)) {
                        if (-not $package.PSObject.Properties.Name.Contains("vulnerabilities")) {
                            continue
                        }

                        foreach ($vulnerability in @($package.vulnerabilities)) {
                            [pscustomobject]@{
                                Project = [string]$project.path
                                Framework = [string]$framework.framework
                                Package = [string]$package.id
                                Version = [string]$package.resolvedVersion
                                Severity = [string]$vulnerability.severity
                                AdvisoryUrl = [string]$vulnerability.advisoryUrl
                            }
                        }
                    }
                }
            }
        }
    )

    $exceptionItems = @()
    if (Test-Path -LiteralPath $ExceptionsPath) {
        $exceptionDocument = Get-Content -LiteralPath $ExceptionsPath -Raw | ConvertFrom-Json
        $exceptionItems = @($exceptionDocument.exceptions)
    }

    $today = [DateTime]::UtcNow.Date
    $invalidExceptions = @()
    foreach ($exception in $exceptionItems) {
        $requiredProperties = @("package", "advisoryUrl", "severity", "expiresOn", "owner", "reason", "mitigation")
        $missing = @($requiredProperties | Where-Object { -not $exception.PSObject.Properties.Name.Contains($_) -or [string]::IsNullOrWhiteSpace([string]$exception.$_) })
        $expiry = [DateTime]::MinValue
        $expiryValid = [DateTime]::TryParseExact(
            [string]$exception.expiresOn,
            "yyyy-MM-dd",
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal,
            [ref]$expiry)

        if ($missing.Count -gt 0 -or -not $expiryValid -or $expiry.Date -lt $today -or @("Critical", "High") -notcontains ([string]$exception.severity)) {
            $invalidExceptions += [string]$exception.advisoryUrl
        }
    }

    if ($invalidExceptions.Count -gt 0) {
        Write-Error ("Invalid, expired, or incomplete dependency exceptions: {0}" -f ($invalidExceptions -join ", "))
        exit 1
    }

    $blockingFindings = @()
    $nonBlockingFindings = @()
    foreach ($finding in $findings) {
        if ([string]::IsNullOrWhiteSpace($finding.Severity)) {
            continue
        }

        if (@("Critical", "High") -contains $finding.Severity) {
            $matchingException = $exceptionItems | Where-Object {
                ([string]$_.package).Equals($finding.Package, [StringComparison]::OrdinalIgnoreCase) -and
                ([string]$_.advisoryUrl).Trim().Equals($finding.AdvisoryUrl.Trim(), [StringComparison]::OrdinalIgnoreCase) -and
                ([string]$_.severity).Equals($finding.Severity, [StringComparison]::OrdinalIgnoreCase)
            } | Select-Object -First 1

            if ($null -eq $matchingException) {
                $blockingFindings += $finding
            }
            else {
                Write-Warning ("Accepted dependency exception: {0} {1} ({2}) — {3}" -f $finding.Package, $finding.Version, $finding.Severity, $finding.AdvisoryUrl)
            }
        }
        else {
            $nonBlockingFindings += $finding
        }
    }

    Write-Host ("Dependency vulnerability scan: {0} finding(s), {1} blocking finding(s)." -f $findings.Count, $blockingFindings.Count)
    foreach ($finding in $findings) {
        Write-Host (" - {0} {1} [{2}] {3} ({4})" -f $finding.Package, $finding.Version, $finding.Severity, $finding.AdvisoryUrl, $finding.Project)
    }

    if ($dotnetExitCode -ne 0 -and $findings.Count -eq 0) {
        $stderr = if (Test-Path -LiteralPath $stderrPath) { Get-Content -LiteralPath $stderrPath -Raw } else { "" }
        throw "dotnet list package failed without reporting a vulnerability. Exit code: $dotnetExitCode. $stderr"
    }

    if ($blockingFindings.Count -gt 0) {
        Write-Error "Unapproved Critical/High dependency vulnerabilities detected. Upgrade the package or add a complete, unexpired exception to $ExceptionsPath."
        exit 1
    }
}
finally {
    if (Test-Path -LiteralPath $stderrPath) {
        Remove-Item -LiteralPath $stderrPath -Force -ErrorAction SilentlyContinue
    }
}
