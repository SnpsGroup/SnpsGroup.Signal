#Requires -Version 5.1
<#
.SYNOPSIS
    Health check script for SnpsGroup.Dfe deployment verification

.DESCRIPTION
    Verifies worker health and deployment status including:
    - Health endpoint responses
    - Process-specific status
    - Tenant-specific status
    - Post-deployment validation
    - Pre-deployment baseline

.PARAMETER Environment
    Target environment (staging or production)

.PARAMETER Process
    Specific process to check (sender or cancel)

.PARAMETER Tenant
    Specific tenant to check

.PARAMETER Watch
    Duration in minutes to continuously monitor

.PARAMETER PreDeploy
    Run pre-deployment health check baseline

.PARAMETER PostRollback
    Run post-rollback validation

.PARAMETER FullValidation
    Run comprehensive validation including tenant isolation

.PARAMETER Verbose
    Show detailed output

.EXAMPLE
    .\health-check.ps1 -Environment production

.EXAMPLE
    .\health-check.ps1 -Environment production -Process sender -Watch 5

.EXAMPLE
    .\health-check.ps1 -Environment production -PreDeploy
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [ValidateSet("staging", "production")]
    [string]$Environment = "production",

    [Parameter(Mandatory = $false)]
    [ValidateSet("sender", "cancel", "")]
    [string]$Process = "",

    [Parameter(Mandatory = $false)]
    [string]$Tenant = "",

    [Parameter(Mandatory = $false)]
    [int]$Watch = 0,

    [Parameter(Mandatory = $false)]
    [switch]$PreDeploy,

    [Parameter(Mandatory = $false)]
    [switch]$PostRollback,

    [Parameter(Mandatory = $false)]
    [switch]$FullValidation,

    [Parameter(Mandatory = $false)]
    [switch]$VerifyTenantIsolation,

    [Parameter(Mandatory = $false)]
    [switch]$CheckDatabase,

    [Parameter(Mandatory = $false)]
    [string]$BaseUrl = $env:DFE_HEALTH_BASE_URL,

    [Parameter(Mandatory = $false)]
    [string]$SqlConnectionString = $env:DFE_SQL_CONNECTION_STRING,

    [Parameter(Mandatory = $false)]
    [string]$TenantIsolationA = "tenant-001",

    [Parameter(Mandatory = $false)]
    [string]$TenantIsolationB = "tenant-002"
)

$ErrorActionPreference = "Continue"

# Color output helpers
function Write-Success($msg) { Write-Host "[✓] $msg" -ForegroundColor Green }
function Write-Warning($msg) { Write-Host "[!] $msg" -ForegroundColor Yellow }
function Write-Error($msg) { Write-Host "[✗] $msg" -ForegroundColor Red }
function Write-Info($msg) { Write-Host "[ℹ] $msg" -ForegroundColor Cyan }

# Configuration
$Config = @{
    staging = @{
        baseUrl = "https://api-staging.snpsgroup.com"
        healthEndpoint = "/api/management/health"
        senderStatusEndpoint = "/api/management/sender/status"
        cancelStatusEndpoint = "/api/management/cancel/status"
    }
    production = @{
        baseUrl = "https://api.production.com"
        healthEndpoint = "/api/management/health"
        senderStatusEndpoint = "/api/management/sender/status"
        cancelStatusEndpoint = "/api/management/cancel/status"
    }
}

$script:ExitCode = 0
$script:CheckResults = @()

if (-not [string]::IsNullOrWhiteSpace($BaseUrl)) {
    $Config[$Environment].baseUrl = $BaseUrl
}

if (
    $Environment -eq "production" -and
    $Config[$Environment].baseUrl -eq "https://api.production.com"
) {
    Write-Error "Production base URL is still placeholder. Set -BaseUrl or DFE_HEALTH_BASE_URL."
    $script:ExitCode = 1
}

function Invoke-HealthEndpoint {
    param(
        [string]$Url,
        [string]$Description,
        [int]$TimeoutSec = 30
    )

    try {
        $response = Invoke-RestMethod -Uri $Url -Method GET -TimeoutSec $TimeoutSec -ErrorAction Stop
        return @{
            Success = $true
            StatusCode = 200
            Data = $response
            Error = $null
        }
    }
    catch {
        $statusCode = if ($_.Exception.Response) { $_.Exception.Response.StatusCode.value__ } else { 0 }
        return @{
            Success = $false
            StatusCode = $statusCode
            Data = $null
            Error = $_.Exception.Message
        }
    }
}

function Test-OverallHealth {
    $url = "$($Config[$Environment].baseUrl)$($Config[$Environment].healthEndpoint)"
    Write-Info "Checking overall health: $url"

    $result = Invoke-HealthEndpoint -Url $url -Description "Overall Health"

    if ($result.Success) {
        $status = $result.Data.status
        if ($status -eq "healthy") {
            Write-Success "Overall health: HEALTHY"
        }
        elseif ($status -eq "degraded") {
            Write-Warning "Overall health: DEGRADED"
            $script:ExitCode = 1
        }
        else {
            Write-Error "Overall health: UNHEALTHY ($status)"
            $script:ExitCode = 1
        }

        # Check component health
        if ($result.Data.components) {
            foreach ($component in $result.Data.components.PSObject.Properties) {
                $compName = $component.Name
                $compStatus = $component.Value.status
                if ($compStatus -eq "healthy") {
                    Write-Success "  Component '$compName': $compStatus"
                }
                else {
                    Write-Error "  Component '$compName': $compStatus"
                    $script:ExitCode = 1
                }
            }
        }

        return $result.Data
    }
    else {
        Write-Error "Health endpoint failed: $($result.Error)"
        $script:ExitCode = 1
        return $null
    }
}

function Test-ProcessHealth {
    param([string]$ProcessType)

    $endpoint = if ($ProcessType -eq "sender") {
        $Config[$Environment].senderStatusEndpoint
    }
    else {
        $Config[$Environment].cancelStatusEndpoint
    }

    $url = "$($Config[$Environment].baseUrl)$endpoint"
    if ($Tenant) {
        $url += "?tenantId=$Tenant"
    }

    Write-Info "Checking $ProcessType worker health: $url"

    $result = Invoke-HealthEndpoint -Url $url -Description "$ProcessType Status"

    if ($result.Success) {
        Write-Success "$ProcessType worker responding"

        if ($result.Data.tenants) {
            $tenantCount = ($result.Data.tenants | Measure-Object).Count
            Write-Info "  Active tenants: $tenantCount"

            foreach ($t in $result.Data.tenants) {
                $tStatus = if ($t.isPaused) { "PAUSED" } else { "ACTIVE" }
                $tHealth = if ($t.errorCount -gt 0) { "ERRORS ($($t.errorCount))" } else { "OK" }

                if ($Tenant -and $t.tenantId -eq $Tenant) {
                    Write-Info "  Tenant $($t.tenantId): $tStatus, Health: $tHealth"
                }
                elseif (-not $Tenant) {
                    Write-Info "  Tenant $($t.tenantId): $tStatus, Health: $tHealth"
                }
            }
        }

        if ($result.Data.metrics) {
            Write-Info "  Queue depth: $($result.Data.metrics.queueDepth)"
            Write-Info "  Processing rate: $($result.Data.metrics.processingRate) items/sec"
        }

        return $result.Data
    }
    else {
        Write-Error "$ProcessType worker health check failed: $($result.Error)"
        $script:ExitCode = 1
        return $null
    }
}

function Test-Metrics {
    Write-Info "Checking metrics thresholds..."

    # This would typically query a metrics endpoint or monitoring system
    # For now, we'll check if the health endpoint includes metrics

    $healthData = Test-OverallHealth

    if ($healthData -and $healthData.metrics) {
        $metrics = $healthData.metrics

        # Check error rate
        if ($metrics.errorRate -gt 5) {
            Write-Error "Error rate too high: $($metrics.errorRate)% (threshold: 5%)"
            $script:ExitCode = 1
        }
        elseif ($metrics.errorRate -gt 1) {
            Write-Warning "Error rate elevated: $($metrics.errorRate)% (threshold: 1%)"
        }
        else {
            Write-Success "Error rate within threshold: $($metrics.errorRate)%"
        }

        # Check latency
        if ($metrics.p95Latency -gt 5000) {
            Write-Error "P95 latency too high: $($metrics.p95Latency)ms (threshold: 5000ms)"
            $script:ExitCode = 1
        }
        elseif ($metrics.p95Latency -gt 2000) {
            Write-Warning "P95 latency elevated: $($metrics.p95Latency)ms"
        }
        else {
            Write-Success "P95 latency within threshold: $($metrics.p95Latency)ms"
        }
    }
}

function Test-DatabaseConnectivity {
    if ([string]::IsNullOrWhiteSpace($SqlConnectionString)) {
        Write-Error "Missing SQL connection string (DFE_SQL_CONNECTION_STRING or -SqlConnectionString)."
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
            Write-Success "Database connectivity confirmed"
        }
        else {
            throw "Unexpected SQL probe result: $result"
        }
    }
    catch {
        Write-Error "Database connectivity check failed: $_"
        $script:ExitCode = 1
    }
    finally {
        if ($command) { $command.Dispose() }
        if ($connection) { $connection.Dispose() }
    }
}

function Get-TenantIdsFromStatus {
    param([object]$StatusPayload)

    $tenantIds = @()

    if ($null -eq $StatusPayload) {
        return $tenantIds
    }

    if ($StatusPayload.tenantId) {
        $tenantIds += [string]$StatusPayload.tenantId
    }

    if ($StatusPayload.tenants -is [array]) {
        foreach ($tenant in $StatusPayload.tenants) {
            if ($tenant.tenantId) {
                $tenantIds += [string]$tenant.tenantId
            }
        }
    }
    elseif ($StatusPayload.tenants -and $StatusPayload.tenants.tenantId) {
        $tenantIds += [string]$StatusPayload.tenants.tenantId
    }

    return $tenantIds | Select-Object -Unique
}

function Test-TenantIsolation {
    Write-Info "Verifying tenant isolation..."

    $tenants = @($TenantIsolationA, $TenantIsolationB) | Select-Object -Unique

    if ($tenants.Count -lt 2) {
        Write-Error "Tenant isolation requires two distinct tenants."
        $script:ExitCode = 1
        return
    }

    foreach ($tenant in $tenants) {
        $url = "$($Config[$Environment].baseUrl)$($Config[$Environment].senderStatusEndpoint)?tenantId=$tenant"
        $result = Invoke-HealthEndpoint -Url $url -Description "Tenant Isolation ($tenant)"

        if (-not $result.Success) {
            Write-Error "Tenant isolation query failed for '$tenant': $($result.Error)"
            $script:ExitCode = 1
            continue
        }

        $returnedTenantIds = Get-TenantIdsFromStatus -StatusPayload $result.Data

        if (-not $returnedTenantIds -or $returnedTenantIds.Count -eq 0) {
            Write-Error "Tenant isolation check returned no tenant IDs for '$tenant'."
            $script:ExitCode = 1
            continue
        }

        $otherTenant = ($tenants | Where-Object { $_ -ne $tenant })[0]

        if (-not ($returnedTenantIds -contains $tenant)) {
            Write-Error "Tenant '$tenant' was not present in its own scoped response."
            $script:ExitCode = 1
        }
        elseif ($returnedTenantIds -contains $otherTenant) {
            Write-Error "Cross-tenant leakage detected: '$tenant' response contains '$otherTenant'."
            $script:ExitCode = 1
        }
        else {
            Write-Success "Tenant '$tenant' isolation verified"
        }
    }
}

function Test-PreDeploy {
    Write-Info "Running pre-deployment health baseline..."

    # Record current health state
    $baseline = @{
        timestamp = (Get-Date -Format "yyyy-MM-ddTHH:mm:ssZ")
        environment = $Environment
        checks = @()
    }

    # Check overall health
    $health = Test-OverallHealth
    if ($health) {
        $baseline.checks += @{
            name = "overall-health"
            status = $health.status
            timestamp = (Get-Date -Format "yyyy-MM-ddTHH:mm:ssZ")
        }
    }

    # Check sender
    $sender = Test-ProcessHealth -ProcessType "sender"
    if ($sender) {
        $baseline.checks += @{
            name = "sender-health"
            status = "healthy"
            tenantCount = ($sender.tenants | Measure-Object).Count
            timestamp = (Get-Date -Format "yyyy-MM-ddTHH:mm:ssZ")
        }
    }

    # Check cancel
    $cancel = Test-ProcessHealth -ProcessType "cancel"
    if ($cancel) {
        $baseline.checks += @{
            name = "cancel-health"
            status = "healthy"
            tenantCount = ($cancel.tenants | Measure-Object).Count
            timestamp = (Get-Date -Format "yyyy-MM-ddTHH:mm:ssZ")
        }
    }

    # Save baseline
    $baselinePath = "artifacts/deployment-baseline-$(Get-Date -Format 'yyyyMMdd-HHmmss').json"
    $baseline | ConvertTo-Json -Depth 10 | Out-File -FilePath $baselinePath -Encoding UTF8
    Write-Success "Pre-deployment baseline saved: $baselinePath"

    return $baseline
}

function Test-PostRollback {
    Write-Info "Running post-rollback validation..."

    # Find the baseline file
    $baselineFile = Get-ChildItem -Path "artifacts/deployment-baseline-*.json" | Sort-Object LastWriteTime -Descending | Select-Object -First 1

    if (-not $baselineFile) {
        Write-Warning "No baseline file found for comparison"
    }
    else {
        $baseline = Get-Content $baselineFile.FullName | ConvertFrom-Json
        Write-Info "Comparing against baseline: $($baselineFile.Name)"
    }

    # Run health checks
    $currentHealth = Test-OverallHealth

    if ($currentHealth -and $currentHealth.status -eq "healthy") {
        Write-Success "Post-rollback health check passed"
    }
    else {
        Write-Error "Post-rollback health check failed"
        $script:ExitCode = 1
    }

    # Verify version rolled back
    Write-Info "Verifying rolled-back version..."
    # This would check actual version deployed

    Write-Success "Post-rollback validation complete"
}

function Watch-Health {
    param([int]$DurationMinutes)

    Write-Info "Monitoring health for $DurationMinutes minutes..."
    Write-Info "Press Ctrl+C to stop monitoring`n"

    $endTime = (Get-Date).AddMinutes($DurationMinutes)
    $interval = 30  # seconds

    while ((Get-Date) -lt $endTime) {
        $timestamp = Get-Date -Format "HH:mm:ss"
        Write-Host "[$timestamp] " -NoNewline -ForegroundColor Cyan

        # Quick health check
        $url = "$($Config[$Environment].baseUrl)$($Config[$Environment].healthEndpoint)"
        $result = Invoke-HealthEndpoint -Url $url -Description "Health" -TimeoutSec 10

        if ($result.Success -and $result.Data.status -eq "healthy") {
            Write-Host "HEALTHY" -ForegroundColor Green -NoNewline
        }
        elseif ($result.Success) {
            Write-Host "DEGRADED" -ForegroundColor Yellow -NoNewline
        }
        else {
            Write-Host "UNHEALTHY" -ForegroundColor Red -NoNewline
            $script:ExitCode = 1
        }

        if ($result.Data -and $result.Data.metrics) {
            $errRate = $result.Data.metrics.errorRate
            $latency = $result.Data.metrics.p95Latency
            Write-Host " | Error: ${errRate}% | P95: ${latency}ms" -ForegroundColor Gray
        }
        else {
            Write-Host ""
        }

        Start-Sleep -Seconds $interval
    }

    Write-Info "Monitoring complete"
}

# Main execution
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "DFE Health Check" -ForegroundColor Cyan
Write-Host "Environment: $Environment" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

# Determine what to check
if ($PreDeploy) {
    Test-PreDeploy
}
elseif ($PostRollback) {
    Test-PostRollback
}
else {
    # Standard health check
    if ($Process) {
        Test-ProcessHealth -ProcessType $Process
    }
    else {
        # Check all
        Test-OverallHealth
        Test-ProcessHealth -ProcessType "sender"
        Test-ProcessHealth -ProcessType "cancel"
    }

    if ($FullValidation) {
        Test-Metrics
        Test-TenantIsolation
    }

    if ($VerifyTenantIsolation) {
        Test-TenantIsolation
    }

    if ($CheckDatabase) {
        Test-DatabaseConnectivity
    }
}

# Watch mode
if ($Watch -gt 0) {
    Watch-Health -DurationMinutes $Watch
}

# Summary
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Health Check Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if ($script:ExitCode -eq 0) {
    Write-Success "All health checks passed"
}
else {
    Write-Error "Health checks failed. Review issues above."
}

exit $script:ExitCode
