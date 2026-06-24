#Requires -Version 5.1
<#
.SYNOPSIS
    Pre-deployment prerequisite verification script for SnpsGroup.Dfe

.DESCRIPTION
    Verifies environment prerequisites before deployment including:
    - Staging/production environment availability
    - Redis connectivity
    - SQL Server database accessibility
    - Load test results validation
    - Configuration validation

.PARAMETER Environment
    Target environment (staging or production)

.PARAMETER CheckConfig
    Include configuration validation checks

.PARAMETER Prereqs
    Comma-separated list of expected prerequisites

.EXAMPLE
    .\verify-prerequisites.ps1 -Environment production

.EXAMPLE
    .\verify-prerequisites.ps1 -Environment staging -CheckConfig
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("staging", "production")]
    [string]$Environment,

    [Parameter(Mandatory = $false)]
    [switch]$CheckConfig,

    [Parameter(Mandatory = $false)]
    [string]$Prereqs = "tenant-data,mocked-sefaz,redis,sql-server",

    [Parameter(Mandatory = $false)]
    [string]$LoadTestOutput = "artifacts/load-tests",
    [Parameter(Mandatory = $false)]
    [string]$AvailablePrereqs = $env:DFE_LOADTEST_AVAILABLE_PREREQS,

    [Parameter(Mandatory = $false)]
    [string]$SqlConnectionString = $env:DFE_SQL_CONNECTION_STRING
)

$ErrorActionPreference = "Stop"

# Color output helpers
function Write-Success($msg) { Write-Host "[✓] $msg" -ForegroundColor Green }
function Write-Warning($msg) { Write-Host "[!] $msg" -ForegroundColor Yellow }
function Write-Error($msg) { Write-Host "[✗] $msg" -ForegroundColor Red }
function Write-Info($msg) { Write-Host "[ℹ] $msg" -ForegroundColor Cyan }

$script:ExitCode = 0
$script:Failures = @()

function Test-Prerequisites {
    param([string[]]$ExpectedPrereqs)

    Write-Info "Checking prerequisites: $($ExpectedPrereqs -join ', ')"

    if ([string]::IsNullOrWhiteSpace($AvailablePrereqs)) {
        Write-Error "Missing DFE_LOADTEST_AVAILABLE_PREREQS (or -AvailablePrereqs)."
        Write-Error "Example: tenant-data,redis,sql-server"
        $script:Failures += "Available prerequisites source not provided"
        $script:ExitCode = 1
        return
    }

    $availablePrereqs = $AvailablePrereqs -split ',' | ForEach-Object { $_.Trim().ToLower() }

    foreach ($prereq in $ExpectedPrereqs) {
        $prereqLower = $prereq.Trim().ToLower()

        if ($prereqLower -eq "mocked-sefaz" -and $Environment -eq "production") {
            Write-Info "Skipping mocked-sefaz prerequisite in production"
            continue
        }

        if ($availablePrereqs -contains $prereqLower) {
            Write-Success "Prerequisite available: $prereq"
        }
        else {
            Write-Error "Prerequisite missing: $prereq"
            $script:Failures += "Missing prerequisite: $prereq"
            $script:ExitCode = 1
        }
    }
}

function Test-LoadTestResults {
    Write-Info "Validating load test results..."

    $summaryFiles = Get-ChildItem -Path "$LoadTestOutput/run-*/summary.json" -ErrorAction SilentlyContinue

    if (-not $summaryFiles) {
        Write-Error "No load test results found in $LoadTestOutput"
        Write-Error "Run load tests before deployment: .\scripts\load-tests\run-load-tests.ps1 -Mode full"
        $script:Failures += "Load test results not found"
        $script:ExitCode = 1
        return
    }

    $summaryFile = $summaryFiles | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $summary = Get-Content $summaryFile.FullName | ConvertFrom-Json

    # Check release gate
    if ($summary.releaseGate.verdict -eq "pass") {
        Write-Success "Load test release gate: PASSED"
    }
    else {
        Write-Error "Load test release gate: FAILED"
        Write-Error "Thresholds violated: $($summary.releaseGate.violatedThresholds -join ', ')"
        $script:Failures += "Load tests failed"
        $script:ExitCode = 1
    }

    # Check specific thresholds
    foreach ($scenario in $summary.scenarios.PSObject.Properties) {
        $scenarioName = $scenario.Name
        $scenarioData = $scenario.Value

        if ($scenarioData.passed) {
            Write-Success "Scenario '$scenarioName' passed"
        }
        else {
            Write-Error "Scenario '$scenarioName' failed"
            $script:Failures += "Scenario failed: $scenarioName"
            $script:ExitCode = 1
        }
    }
}

function Test-RedisConnectivity {
    Write-Info "Checking Redis connectivity..."

    try {
        # Try to ping Redis using redis-cli
        $result = & redis-cli ping 2>&1
        if ($result -eq "PONG") {
            Write-Success "Redis connectivity confirmed"
        }
        else {
            Write-Error "Redis ping failed: $result"
            $script:Failures += "Redis connectivity failed"
            $script:ExitCode = 1
        }
    }
    catch {
        Write-Warning "Redis-cli not available or Redis connection failed: $_"
        Write-Warning "Ensure Redis is accessible before deployment"
    }
}

function Test-DatabaseConnectivity {
    Write-Info "Checking SQL Server connectivity..."

    if ([string]::IsNullOrWhiteSpace($SqlConnectionString)) {
        Write-Error "Missing SQL connection string (DFE_SQL_CONNECTION_STRING or -SqlConnectionString)."
        $script:Failures += "Database connection string not configured"
        $script:ExitCode = 1
        return
    }

    $connection = $null
    $command = $null
    try {
        $connectionType =
            [Type]::GetType("Microsoft.Data.SqlClient.SqlConnection, Microsoft.Data.SqlClient")
        if (-not $connectionType) {
            $connectionType = [Type]::GetType("System.Data.SqlClient.SqlConnection, System.Data")
        }

        if (-not $connectionType) {
            throw "No SQL client assembly found (Microsoft.Data.SqlClient/System.Data.SqlClient)."
        }

        $connection = [Activator]::CreateInstance($connectionType, $SqlConnectionString)
        $connection.Open()

        $command = $connection.CreateCommand()
        $command.CommandText = "SELECT 1"
        $result = $command.ExecuteScalar()

        if ([int]$result -eq 1) {
            Write-Success "SQL Server connectivity confirmed"
        }
        else {
            throw "Unexpected SQL probe result: $result"
        }
    }
    catch {
        Write-Error "Database connectivity failed: $_"
        $script:Failures += "Database connectivity failed"
        $script:ExitCode = 1
    }
    finally {
        if ($command) { $command.Dispose() }
        if ($connection) { $connection.Dispose() }
    }
}

function Test-Configuration {
    Write-Info "Checking configuration management..."

    # Check versioned profiles exist
    $profiles = @("sender/v1", "cancel/v1")
    foreach ($profile in $profiles) {
        try {
            $result = & redis-cli GET "config:$profile" 2>&1
            if ($result -and $result -notlike "*nil*") {
                Write-Success "Configuration profile exists: $profile"
            }
            else {
                Write-Warning "Configuration profile not found: $profile (may be created on first startup)"
            }
        }
        catch {
            Write-Warning "Could not verify configuration profile: $profile"
        }
    }

    # Check feature flags key exists
    try {
        $result = & redis-cli KEYS "featureflags:*" 2>&1
        if ($result) {
            Write-Success "Feature flags configuration found"
        }
        else {
            Write-Warning "No feature flags configured (may be created on first use)"
        }
    }
    catch {
        Write-Warning "Could not verify feature flags"
    }
}

function Test-DeploymentReadiness {
    Write-Info "Checking deployment readiness..."

    # Check if previous deployment completed successfully
    $lastDeployFile = "artifacts/last-deployment.json"
    if (Test-Path $lastDeployFile) {
        $lastDeploy = Get-Content $lastDeployFile | ConvertFrom-Json
        $timeSinceDeploy = (Get-Date) - [datetime]$lastDeploy.timestamp

        if ($timeSinceDeploy.TotalMinutes -lt 30) {
            Write-Warning "Previous deployment was $(($timeSinceDeploy.TotalMinutes).ToString('F0')) minutes ago"
            Write-Warning "Ensure sufficient time has passed between deployments"
        }
        else {
            Write-Success "Sufficient time since last deployment: $(($timeSinceDeploy.TotalMinutes).ToString('F0')) minutes"
        }
    }
}

# Main execution
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "DFE Deployment Prerequisite Verification" -ForegroundColor Cyan
Write-Host "Environment: $Environment" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

# 1. Check prerequisites
$expectedPrereqs = $Prereqs -split ',' | ForEach-Object { $_.Trim() }
Test-Prerequisites -ExpectedPrereqs $expectedPrereqs

# 2. Load test validation
Test-LoadTestResults

# 3. Infrastructure checks
Test-RedisConnectivity
Test-DatabaseConnectivity

# 4. Configuration validation (optional)
if ($CheckConfig) {
    Test-Configuration
}

# 5. Deployment readiness
Test-DeploymentReadiness

# Summary
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Verification Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if ($script:ExitCode -eq 0) {
    Write-Success "All prerequisites verified. Ready for deployment."
    Write-Host "`nNext steps:" -ForegroundColor Cyan
    Write-Host "  1. Review load test report: $LoadTestOutput/run-*/report.md" -ForegroundColor White
    Write-Host "  2. Run deployment: Follow docs/rollout-playbook.md" -ForegroundColor White
    Write-Host "  3. Monitor deployment: ./scripts/deployment/health-check.ps1" -ForegroundColor White
}
else {
    Write-Error "Prerequisite verification failed with $($script:Failures.Count) issue(s):"
    foreach ($failure in $script:Failures) {
        Write-Error "  - $failure"
    }
    Write-Host "`nResolve the above issues before proceeding with deployment." -ForegroundColor Yellow
}

exit $script:ExitCode
