#Requires -Version 5.1
<#
.SYNOPSIS
    Rollback script for SnpsGroup.Dfe deployment

.DESCRIPTION
    Performs rollback operations including:
    - Configuration rollback (versioned profiles in Redis)
    - Service rollback (redeploy previous version)
    - State validation after rollback

.PARAMETER Type
    Rollback type: config, service, or full

.PARAMETER TargetVersion
    Target version to rollback to (e.g., "v1", "1.2.2")

.PARAMETER Environment
    Target environment (staging or production)

.PARAMETER Process
    Specific process to rollback (sender, cancel, or both)

.PARAMETER Force
    Force rollback without confirmation

.PARAMETER ValidateOnly
    Only validate rollback state, do not execute

.EXAMPLE
    .\rollback.ps1 -Type config -TargetVersion v1 -Environment production

.EXAMPLE
    .\rollback.ps1 -Type full -TargetVersion 1.2.2 -Environment production -Process sender

.EXAMPLE
    .\rollback.ps1 -Type full -TargetVersion 1.2.2 -Environment production -Force
#>

[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("config", "service", "full")]
    [string]$Type,

    [Parameter(Mandatory = $true)]
    [string]$TargetVersion,

    [Parameter(Mandatory = $false)]
    [ValidateSet("staging", "production")]
    [string]$Environment = "production",

    [Parameter(Mandatory = $false)]
    [ValidateSet("sender", "cancel", "both")]
    [string]$Process = "both",

    [Parameter(Mandatory = $false)]
    [switch]$Force,

    [Parameter(Mandatory = $false)]
    [switch]$ValidateOnly,

    [Parameter(Mandatory = $false)]
    [ValidateSet("azure-devops", "kubernetes", "custom")]
    [string]$ServiceRollbackBackend = "azure-devops",

    [Parameter(Mandatory = $false)]
    [string]$SenderPipelineName = "Deploy-Sender-Worker",

    [Parameter(Mandatory = $false)]
    [string]$CancelPipelineName = "Deploy-Cancel-Worker",

    [Parameter(Mandatory = $false)]
    [string]$KubernetesNamespace = "dfe-production",

    [Parameter(Mandatory = $false)]
    [string]$SenderDeploymentName = "sender-worker",

    [Parameter(Mandatory = $false)]
    [string]$CancelDeploymentName = "cancel-worker",

    [Parameter(Mandatory = $false)]
    [string]$CustomServiceRollbackCommand = ""
)

$ErrorActionPreference = "Stop"

# Color output helpers
function Write-Success($msg) { Write-Host "[✓] $msg" -ForegroundColor Green }
function Write-Warning($msg) { Write-Host "[!] $msg" -ForegroundColor Yellow }
function Write-Error($msg) { Write-Host "[✗] $msg" -ForegroundColor Red }
function Write-Info($msg) { Write-Host "[ℹ] $msg" -ForegroundColor Cyan }
function Write-Action($msg) { Write-Host "[→] $msg" -ForegroundColor Magenta }

$script:ExitCode = 0
$script:StartTime = Get-Date

function Get-ElapsedTime {
    $elapsed = (Get-Date) - $script:StartTime
    return "{0:mm\:ss}" -f $elapsed
}

function Test-Confirmation {
    if ($Force) {
        return $true
    }

    Write-Warning "This will perform a $Type rollback to version $TargetVersion in $Environment"
    Write-Warning "Estimated RTO: $(if ($Type -eq 'config') { '< 5 minutes' } else { '< 15 minutes' })"
    Write-Host ""

    $confirmation = Read-Host "Are you sure you want to proceed? (yes/no)"
    return ($confirmation -eq "yes")
}

function Invoke-ConfigRollback {
    param(
        [string]$ProcessType,
        [string]$Version
    )

    Write-Action "Rolling back configuration for $ProcessType to version $Version..."

    try {
        # Set the current version in Redis
        $redisKey = "config:$ProcessType`:current"
        $result = & redis-cli SET $redisKey $Version 2>&1

        if ($result -eq "OK") {
            Write-Success "Configuration rollback successful for $ProcessType"
            Write-Info "New version: $Version"
            Write-Info "Workers will pick up new configuration automatically"
        }
        else {
            Write-Error "Redis SET failed: $result"
            $script:ExitCode = 1
        }

        # Verify the change
        $verify = & redis-cli GET $redisKey 2>&1
        if ($verify -eq $Version) {
            Write-Success "Configuration verified in Redis"
        }
        else {
            Write-Error "Configuration verification failed. Expected: $Version, Got: $verify"
            $script:ExitCode = 1
        }
    }
    catch {
        Write-Error "Configuration rollback failed: $_"
        $script:ExitCode = 1
    }
}

function Invoke-ServiceRollback {
    param(
        [string]$ProcessType,
        [string]$Version
    )

    Write-Action "Rolling back service for $ProcessType to version $Version..."

    try {
        switch ($ServiceRollbackBackend) {
            "azure-devops" {
                if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
                    throw "Azure CLI (az) not found. Install/configure it or use -ServiceRollbackBackend kubernetes/custom."
                }

                $pipelineName = if ($ProcessType -eq "sender") { $SenderPipelineName } else { $CancelPipelineName }
                Write-Info "Triggering Azure DevOps rollback pipeline: $pipelineName"

                $azOutput = & az pipelines run `
                    --name $pipelineName `
                    --variables "environment=$Environment" "version=$Version" "rollback=true" "process=$ProcessType" `
                    --output json 2>&1

                if ($LASTEXITCODE -ne 0) {
                    throw "Azure DevOps rollback trigger failed: $($azOutput -join ' ')"
                }

                $runData = $null
                try {
                    $runData = ($azOutput -join "`n") | ConvertFrom-Json
                }
                catch {
                    $runData = $null
                }

                if ($runData -and $runData.id) {
                    Write-Success "Rollback pipeline triggered (run id: $($runData.id))"
                }
                else {
                    Write-Success "Rollback pipeline triggered"
                }
            }
            "kubernetes" {
                if (-not (Get-Command kubectl -ErrorAction SilentlyContinue)) {
                    throw "kubectl not found. Install/configure it or use -ServiceRollbackBackend azure-devops/custom."
                }

                $deploymentName = if ($ProcessType -eq "sender") { $SenderDeploymentName } else { $CancelDeploymentName }
                Write-Info "Rolling back Kubernetes deployment '$deploymentName' in namespace '$KubernetesNamespace'"

                $undoOutput = & kubectl rollout undo "deployment/$deploymentName" -n $KubernetesNamespace 2>&1
                if ($LASTEXITCODE -ne 0) {
                    throw "kubectl rollout undo failed: $($undoOutput -join ' ')"
                }

                $statusOutput = & kubectl rollout status "deployment/$deploymentName" -n $KubernetesNamespace --timeout=300s 2>&1
                if ($LASTEXITCODE -ne 0) {
                    throw "kubectl rollout status failed: $($statusOutput -join ' ')"
                }

                Write-Success "Kubernetes rollback completed for $ProcessType"
            }
            "custom" {
                if ([string]::IsNullOrWhiteSpace($CustomServiceRollbackCommand)) {
                    throw "Custom rollback command is empty. Set -CustomServiceRollbackCommand."
                }

                $commandText = $CustomServiceRollbackCommand
                $commandText = $commandText.Replace("{process}", $ProcessType)
                $commandText = $commandText.Replace("{version}", $Version)
                $commandText = $commandText.Replace("{environment}", $Environment)

                Write-Info "Executing custom rollback command for $ProcessType"
                $customOutput = & pwsh -NoProfile -Command $commandText 2>&1
                if ($LASTEXITCODE -ne 0) {
                    throw "Custom rollback command failed: $($customOutput -join ' ')"
                }

                Write-Success "Custom rollback command completed for $ProcessType"
            }
            default {
                throw "Unsupported rollback backend: $ServiceRollbackBackend"
            }
        }
    }
    catch {
        Write-Error "Service rollback failed: $_"
        $script:ExitCode = 1
    }
}

function Invoke-FeatureFlagRollback {
    Write-Action "Disabling feature flags..."

    try {
        # Disable all feature flags as a safety measure
        $flags = & redis-cli KEYS "featureflags:*" 2>&1

        if ($flags) {
            foreach ($flag in $flags) {
                Write-Info "Disabling feature flag: $flag"
                # Set default to false
                & redis-cli HSET "$flag" "_default" "false" | Out-Null
            }
            Write-Success "Feature flags disabled"
        }
        else {
            Write-Info "No feature flags found"
        }
    }
    catch {
        Write-Warning "Could not disable feature flags: $_"
    }
}

function Test-RollbackState {
    Write-Action "Validating rollback state..."

    # Wait a moment for changes to propagate
    Write-Info "Waiting for changes to propagate..."
    Start-Sleep -Seconds 5

    # Check configuration version
    if ($Type -eq "config" -or $Type -eq "full") {
        $processes = if ($Process -eq "both") { @("sender", "cancel") } else { @($Process) }

        foreach ($proc in $processes) {
            $redisKey = "config:$proc`:current"
            $currentVersion = & redis-cli GET $redisKey 2>&1

            if ($currentVersion -eq $TargetVersion) {
                Write-Success "Configuration version verified for $proc`: $currentVersion"
            }
            else {
                Write-Error "Configuration version mismatch for $proc. Expected: $TargetVersion, Got: $currentVersion"
                $script:ExitCode = 1
            }
        }
    }

    # Check service health
    if ($Type -eq "service" -or $Type -eq "full") {
        Write-Info "Checking service health..."

        # Run health check
        $healthCheckScript = Join-Path $PSScriptRoot "health-check.ps1"
        if (Test-Path $healthCheckScript) {
            & $healthCheckScript -Environment $Environment -PostRollback
            if ($LASTEXITCODE -ne 0) {
                Write-Error "Post-rollback health check failed"
                $script:ExitCode = 1
            }
        }
        else {
            Write-Warning "Health check script not found: $healthCheckScript"
        }
    }
}

function Write-RollbackSummary {
    $elapsed = Get-ElapsedTime

    Write-Host "`n========================================" -ForegroundColor Cyan
    Write-Host "Rollback Summary" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan

    Write-Info "Rollback Type: $Type"
    Write-Info "Target Version: $TargetVersion"
    Write-Info "Environment: $Environment"
    Write-Info "Process(es): $Process"
    Write-Info "Elapsed Time: $elapsed"

    if ($script:ExitCode -eq 0) {
        Write-Success "Rollback completed successfully"

        # Calculate RTO
        $rto = [int]((Get-Date) - $script:StartTime).TotalMinutes
        Write-Info "Actual RTO: $rto minutes"

        Write-Host "`nNext steps:" -ForegroundColor Cyan
        Write-Host "  1. Monitor metrics and error rates for 15 minutes" -ForegroundColor White
        Write-Host "  2. Verify all tenants processing normally" -ForegroundColor White
        Write-Host "  3. Document rollback in incident log" -ForegroundColor White
        Write-Host "  4. Schedule post-incident review" -ForegroundColor White
    }
    else {
        Write-Error "Rollback completed with errors. Review output above."
        Write-Warning "Manual intervention may be required"
    }
}

# Main execution
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "DFE Rollback Procedure" -ForegroundColor Cyan
Write-Host "Type: $Type | Version: $TargetVersion | Environment: $Environment" -ForegroundColor Cyan
Write-Host "========================================`n" -ForegroundColor Cyan

# Validate-only mode
if ($ValidateOnly) {
    Write-Info "Running in validation-only mode"
    Test-RollbackState
    exit $script:ExitCode
}

# Confirmation
if (-not (Test-Confirmation)) {
    Write-Warning "Rollback cancelled by user"
    exit 0
}

# Determine processes to rollback
$processesToRollback = if ($Process -eq "both") { @("sender", "cancel") } else { @($Process) }

# Execute rollback
switch ($Type) {
    "config" {
        foreach ($proc in $processesToRollback) {
            Invoke-ConfigRollback -ProcessType $proc -Version $TargetVersion
        }
    }

    "service" {
        # Disable feature flags first for safety
        Invoke-FeatureFlagRollback

        foreach ($proc in $processesToRollback) {
            Invoke-ServiceRollback -ProcessType $proc -Version $TargetVersion
        }
    }

    "full" {
        # Full rollback = config + service

        # Step 1: Disable feature flags
        Invoke-FeatureFlagRollback

        # Step 2: Rollback configuration
        foreach ($proc in $processesToRollback) {
            Invoke-ConfigRollback -ProcessType $proc -Version $TargetVersion
        }

        # Step 3: Wait for config to take effect
        Write-Info "Waiting for configuration rollback to take effect..."
        Start-Sleep -Seconds 10

        # Step 4: Rollback services
        foreach ($proc in $processesToRollback) {
            Invoke-ServiceRollback -ProcessType $proc -Version $TargetVersion
        }
    }
}

# Validate rollback
Test-RollbackState

# Write summary
Write-RollbackSummary

# Record rollback for audit
$rollbackRecord = @{
    timestamp = (Get-Date -Format "yyyy-MM-ddTHH:mm:ssZ")
    type = $Type
    targetVersion = $TargetVersion
    environment = $Environment
    process = $Process
    elapsedMinutes = [int]((Get-Date) - $script:StartTime).TotalMinutes
    success = ($script:ExitCode -eq 0)
    user = $env:USERNAME
}

$auditFile = "artifacts/rollback-history.json"
$auditDir = Split-Path $auditFile -Parent
if (-not (Test-Path $auditDir)) {
    New-Item -ItemType Directory -Path $auditDir -Force | Out-Null
}

$history = @()
if (Test-Path $auditFile) {
    $history = Get-Content $auditFile | ConvertFrom-Json
    if ($history -isnot [array]) { $history = @($history) }
}
$history += $rollbackRecord
$history | ConvertTo-Json -Depth 10 | Out-File -FilePath $auditFile -Encoding UTF8

Write-Info "Rollback recorded in: $auditFile"

exit $script:ExitCode
