# Deploy-SnpsGroupApp.ps1
# Script completo de deployment da aplicação SnpsGroup.Adaptive
param(
    [Parameter(Mandatory=$true)]
    [ValidateSet("Production", "Staging")]
    [string]$Environment,
    
    [Parameter(Mandatory=$true)]
    [string]$Version,
    
    [Parameter(Mandatory=$true)]
    [string]$ArtifactPath,
    
    [string]$CloudProvider = "winov",
    [string]$Repository = "SnpsGroup.Dfe",
    [int]$HealthCheckRetries = 3,
    [int]$HealthCheckDelay = 10,
    [switch]$SkipBackup,
    [switch]$WhatIf
)

# Configurações por ambiente
$environmentConfig = @{
    Production = @{
        WebsiteName = "AdaptiveDfe"
        AppPoolName = "AdaptiveDfe"
        DomainName = "def.snpsgroup.com"
        VersionSuffix = "400"
        Port = 443
        Protocol = "https"
    }
    Staging = @{
        WebsiteName = "StageAdaptiveDfe"
        AppPoolName = "StageAdaptiveDfe"
        DomainName = "stage-dfe.snpsgroup.com"
        VersionSuffix = "200"
        Port = 443
        Protocol = "https"
    }
}

function Write-DeploymentLog {
    param(
        [string]$Message,
        [ValidateSet("Info", "Warning", "Error", "Success")]
        [string]$Level = "Info"
    )
    
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    $prefix = switch ($Level) {
        "Info" { "ℹ️" }
        "Warning" { "⚠️" }
        "Error" { "❌" }
        "Success" { "✅" }
    }
    
    Write-Host "[$timestamp] $prefix $Message"
    
    # Log para arquivo também
    $logPath = "C:\AzureDevops\Logs\deployment-$(Get-Date -Format 'yyyyMMdd').log"
    if (!(Test-Path (Split-Path $logPath))) {
        New-Item -Path (Split-Path $logPath) -ItemType Directory -Force | Out-Null
    }
    "[$timestamp] [$Level] $Message" | Add-Content -Path $logPath
}

function Test-Prerequisites {
    Write-DeploymentLog "🔍 Verificando pré-requisitos..."
    
    # Verificar se o artifact path existe
    if (!(Test-Path $ArtifactPath)) {
        throw "Artifact path não encontrado: $ArtifactPath"
    }
    
    # Verificar se há arquivos no artifact
    $files = Get-ChildItem -Path $ArtifactPath -Recurse -File
    if ($files.Count -eq 0) {
        throw "Nenhum arquivo encontrado no artifact path: $ArtifactPath"
    }
    
    # Verificar módulos PowerShell necessários
    $requiredModules = @("WebAdministration", "IISAdministration")
    foreach ($module in $requiredModules) {
        try {
            Import-Module $module -ErrorAction Stop
            Write-DeploymentLog "Módulo $module importado com sucesso"
        }
        catch {
            throw "Falha ao importar módulo $module`: $($_.Exception.Message)"
        }
    }
    
    # Verificar permissões de escrita
    $testFile = "C:\AzureDevops\test-permissions.tmp"
    try {
        "test" | Out-File -FilePath $testFile -Force
        Remove-Item $testFile -Force
        Write-DeploymentLog "Permissões de escrita verificadas"
    }
    catch {
        throw "Sem permissões de escrita em C:\AzureDevops"
    }
    
    Write-DeploymentLog "Pré-requisitos verificados com sucesso" -Level Success
}

function Backup-CurrentVersion {
    param([string]$WebsiteName, [string]$BackupPath)
    
    if ($SkipBackup) {
        Write-DeploymentLog "Backup ignorado conforme solicitado" -Level Warning
        return
    }
    
    Write-DeploymentLog "📦 Criando backup da versão atual..."
    
    try {
        $website = Get-Website -Name $WebsiteName -ErrorAction SilentlyContinue
        if ($website) {
            $currentPath = $website.PhysicalPath
            if (Test-Path $currentPath) {
                $backupTimestamp = Get-Date -Format "yyyyMMdd-HHmmss"
                $backupDestination = "$BackupPath\backup-$backupTimestamp"
                
                Copy-Item -Path $currentPath -Destination $backupDestination -Recurse -Force
                Write-DeploymentLog "Backup criado em: $backupDestination" -Level Success
                
                # Manter apenas os últimos 5 backups
                $backups = Get-ChildItem -Path $BackupPath -Directory | 
                          Where-Object { $_.Name -like "backup-*" } | 
                          Sort-Object CreationTime -Descending
                
                if ($backups.Count -gt 5) {
                    $backups | Select-Object -Skip 5 | Remove-Item -Recurse -Force
                    Write-DeploymentLog "Backups antigos removidos (mantidos últimos 5)"
                }
                
                return $backupDestination
            }
        }
    }
    catch {
        Write-DeploymentLog "Erro ao criar backup: $($_.Exception.Message)" -Level Warning
    }
}

function Stop-ApplicationSafely {
    param([string]$WebsiteName, [string]$AppPoolName)
    
    Write-DeploymentLog "⏹️ Parando aplicação $WebsiteName..."
    
    try {
        # Parar website
        $website = Get-Website -Name $WebsiteName -ErrorAction SilentlyContinue
        if ($website -and $website.State -eq "Started") {
            Stop-Website -Name $WebsiteName
            Write-DeploymentLog "Website $WebsiteName parado"
        }
        
        # Parar application pool
        $appPool = Get-IISAppPool -Name $AppPoolName -ErrorAction SilentlyContinue
        if ($appPool -and $appPool.State -eq "Started") {
            Stop-WebAppPool -Name $AppPoolName
            
            # Aguardar o pool parar completamente
            $timeout = 30
            $elapsed = 0
            while ((Get-WebAppPool -Name $AppPoolName).State -ne "Stopped" -and $elapsed -lt $timeout) {
                Start-Sleep -Seconds 1
                $elapsed++
            }
            
            Write-DeploymentLog "Application Pool $AppPoolName parado"
        }
        
        # Aguardar um pouco para garantir que todos os handles de arquivo foram liberados
        Start-Sleep -Seconds 5
        
        Write-DeploymentLog "Aplicação parada com sucesso" -Level Success
    }
    catch {
        Write-DeploymentLog "Erro ao parar aplicação: $($_.Exception.Message)" -Level Error
        throw
    }
}

function Deploy-Application {
    param(
        [string]$SourcePath,
        [string]$DestinationPath,
        [string]$Environment
    )
    
    Write-DeploymentLog "🚀 Iniciando deployment para $DestinationPath..."
    
    try {
        # Criar diretório de destino se não existir
        if (!(Test-Path $DestinationPath)) {
            New-Item -Path $DestinationPath -ItemType Directory -Force | Out-Null
            Write-DeploymentLog "Diretório de destino criado: $DestinationPath"
        }
        
        # Copiar todos os arquivos
        $sourceFiles = Get-ChildItem -Path $SourcePath -Recurse
        $totalFiles = $sourceFiles.Count
        $copiedFiles = 0
        
        Write-DeploymentLog "Copiando $totalFiles arquivos..."
        
        foreach ($file in $sourceFiles) {
            $relativePath = $file.FullName.Substring($SourcePath.Length + 1)
            $destFile = Join-Path $DestinationPath $relativePath
            $destDir = Split-Path $destFile -Parent
            
            if (!(Test-Path $destDir)) {
                New-Item -Path $destDir -ItemType Directory -Force | Out-Null
            }
            
            if ($file -is [System.IO.FileInfo]) {
                Copy-Item -Path $file.FullName -Destination $destFile -Force
                $copiedFiles++
                
                if ($copiedFiles % 100 -eq 0) {
                    Write-DeploymentLog "Progresso: $copiedFiles/$totalFiles arquivos copiados"
                }
            }
        }
        
        Write-DeploymentLog "Deployment concluído: $copiedFiles arquivos copiados" -Level Success
        
        # Aplicar configuração específica do ambiente
        Apply-EnvironmentConfiguration -DeploymentPath $DestinationPath -Environment $Environment
        
    }
    catch {
        Write-DeploymentLog "Erro durante deployment: $($_.Exception.Message)" -Level Error
        throw
    }
}

function Apply-EnvironmentConfiguration {
    param([string]$DeploymentPath, [string]$Environment)
    
    Write-DeploymentLog "⚙️ Aplicando configuração para ambiente $Environment..."
    
    try {
        # Aplicar appsettings específico do ambiente
        $appSettingsEnv = Join-Path $DeploymentPath "appsettings.$Environment.json"
        $appSettingsMain = Join-Path $DeploymentPath "appsettings.json"
        
        if (Test-Path $appSettingsEnv) {
            Copy-Item -Path $appSettingsEnv -Destination $appSettingsMain -Force
            Write-DeploymentLog "appsettings.$Environment.json aplicado"
        } else {
            Write-DeploymentLog "appsettings.$Environment.json não encontrado" -Level Warning
        }
        
        # Configurar permissões no diretório
        $currentUser = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        icacls $DeploymentPath /grant "IIS_IUSRS:(OI)(CI)RX" /T | Out-Null
        icacls $DeploymentPath /grant "IUSR:(OI)(CI)RX" /T | Out-Null
        
        Write-DeploymentLog "Permissões configuradas para IIS"
        
        # Criar diretório de logs se não existir
        $logsPath = Join-Path $DeploymentPath "logs"
        if (!(Test-Path $logsPath)) {
            New-Item -Path $logsPath -ItemType Directory -Force | Out-Null
            icacls $logsPath /grant "IIS_IUSRS:(OI)(CI)F" /T | Out-Null
            Write-DeploymentLog "Diretório de logs criado com permissões"
        }
        
        Write-DeploymentLog "Configuração de ambiente aplicada" -Level Success
    }
    catch {
        Write-DeploymentLog "Erro ao aplicar configuração: $($_.Exception.Message)" -Level Error
        throw
    }
}

function Start-ApplicationSafely {
    param([string]$WebsiteName, [string]$AppPoolName)
    
    Write-DeploymentLog "▶️ Iniciando aplicação $WebsiteName..."
    
    try {
        # Iniciar application pool primeiro
        $appPool = Get-WebAppPool -Name $AppPoolName
        if ($appPool.State -ne "Started") {
            Start-WebAppPool -Name $AppPoolName
            
            # Aguardar o pool iniciar
            $timeout = 60
            $elapsed = 0
            while ((Get-WebAppPool -Name $AppPoolName).State -ne "Started" -and $elapsed -lt $timeout) {
                Start-Sleep -Seconds 1
                $elapsed++
            }
            
            if ($elapsed -ge $timeout) {
                throw "Timeout ao iniciar Application Pool $AppPoolName"
            }
            
            Write-DeploymentLog "Application Pool $AppPoolName iniciado"
        }
        
        # Iniciar website
        $website = Get-Website -Name $WebsiteName
        if ($website.State -ne "Started") {
            Start-Website -Name $WebsiteName
            Write-DeploymentLog "Website $WebsiteName iniciado"
        }
        
        # Aguardar um pouco para a aplicação inicializar
        Start-Sleep -Seconds 10
        
        Write-DeploymentLog "Aplicação iniciada com sucesso" -Level Success
    }
    catch {
        Write-DeploymentLog "Erro ao iniciar aplicação: $($_.Exception.Message)" -Level Error
        throw
    }
}

function Test-ApplicationHealth {
    param([string]$Url, [int]$Retries = 3, [int]$DelaySeconds = 10)
    
    Write-DeploymentLog "🏥 Executando health check em $Url..."
    
    for ($i = 1; $i -le $Retries; $i++) {
        try {
            Write-DeploymentLog "Health check tentativa $i/$Retries..."
            
            $response = Invoke-WebRequest -Uri "$Url/health" -TimeoutSec 30 -UseBasicParsing
            
            if ($response.StatusCode -eq 200) {
                Write-DeploymentLog "Health check passou (Status: $($response.StatusCode))" -Level Success
                
                # Tentar também endpoint de versão se disponível
                try {
                    $versionResponse = Invoke-WebRequest -Uri "$Url/version" -TimeoutSec 10 -UseBasicParsing
                    Write-DeploymentLog "Versão da aplicação: $($versionResponse.Content)"
                }
                catch {
                    Write-DeploymentLog "Endpoint de versão não disponível" -Level Warning
                }
                
                return $true
            } else {
                Write-DeploymentLog "Health check retornou status $($response.StatusCode)" -Level Warning
            }
        }
        catch {
            Write-DeploymentLog "Health check falhou: $($_.Exception.Message)" -Level Warning
        }
        
        if ($i -lt $Retries) {
            Write-DeploymentLog "Aguardando $DelaySeconds segundos antes da próxima tentativa..."
            Start-Sleep -Seconds $DelaySeconds
        }
    }
    
    Write-DeploymentLog "Health check falhou após $Retries tentativas" -Level Error
    return $false
}

function Invoke-Rollback {
    param([string]$BackupPath, [string]$WebsiteName, [string]$AppPoolName)
    
    if (!$BackupPath -or !(Test-Path $BackupPath)) {
        Write-DeploymentLog "Backup não disponível para rollback" -Level Error
        return $false
    }
    
    Write-DeploymentLog "🔄 Executando rollback para $BackupPath..." -Level Warning
    
    try {
        # Parar aplicação
        Stop-ApplicationSafely -WebsiteName $WebsiteName -AppPoolName $AppPoolName
        
        # Restaurar backup
        $website = Get-Website -Name $WebsiteName
        $currentPath = $website.PhysicalPath
        
        # Remover deployment atual
        if (Test-Path $currentPath) {
            Remove-Item -Path $currentPath -Recurse -Force
        }
        
        # Restaurar do backup
        Copy-Item -Path $BackupPath -Destination $currentPath -Recurse -Force
        
        # Reiniciar aplicação
        Start-ApplicationSafely -WebsiteName $WebsiteName -AppPoolName $AppPoolName
        
        Write-DeploymentLog "Rollback concluído com sucesso" -Level Success
        return $true
    }
    catch {
        Write-DeploymentLog "Erro durante rollback: $($_.Exception.Message)" -Level Error
        return $false
    }
}

# ===============================
# EXECUÇÃO PRINCIPAL
# ===============================

try {
    $config = $environmentConfig[$Environment]
    $fullVersion = "$Version.$($config.VersionSuffix)"
    $deploymentPath = "C:\AzureDevops\$Environment\$Repository\$fullVersion"
    $backupPath = "C:\AzureDevops\Backups\$Environment"
    $healthUrl = "$($config.Protocol)://$($config.DomainName)"
    
    Write-DeploymentLog "🚀 Iniciando deployment SnpsGroup.Adaptive" -Level Success
    Write-DeploymentLog "Ambiente: $Environment"
    Write-DeploymentLog "Versão: $fullVersion"
    Write-DeploymentLog "Destino: $deploymentPath"
    Write-DeploymentLog "Artifact: $ArtifactPath"
    
    if ($WhatIf) {
        Write-DeploymentLog "=== MODO WHAT-IF ATIVO ===" -Level Warning
        Write-DeploymentLog "Deployment seria executado com as configurações acima"
        Write-DeploymentLog "Website: $($config.WebsiteName)"
        Write-DeploymentLog "App Pool: $($config.AppPoolName)"
        Write-DeploymentLog "Health URL: $healthUrl/health"
        exit 0
    }
    
    # Pré-requisitos
    Test-Prerequisites
    
    # Importar script de configuração IIS
    $iisScriptPath = Join-Path (Split-Path $PSScriptRoot) "Initialize-SnpsGroupIIS.ps1"
    if (Test-Path $iisScriptPath) {
        . $iisScriptPath
        Write-DeploymentLog "Script IIS importado"
    }
    
    # Configurar IIS
    Write-DeploymentLog "Configurando IIS..."
    $iisResult = Initialize-SnpsGroupIIS -Environment $Environment -Version $Version -CloudProvider $CloudProvider -Repository $Repository
    if (!$iisResult.Success) {
        throw "Falha na configuração IIS: $($iisResult.Error)"
    }
    
    # Backup da versão atual
    $backupLocation = Backup-CurrentVersion -WebsiteName $config.WebsiteName -BackupPath $backupPath
    
    # Parar aplicação
    Stop-ApplicationSafely -WebsiteName $config.WebsiteName -AppPoolName $config.AppPoolName
    
    # Deploy da nova versão
    Deploy-Application -SourcePath $ArtifactPath -DestinationPath $deploymentPath -Environment $Environment
    
    # Atualizar IIS para apontar para nova versão
    Set-ItemProperty -Path "IIS:\Sites\$($config.WebsiteName)" -name "physicalPath" -value $deploymentPath
    Write-DeploymentLog "IIS atualizado para nova versão"
    
    # Iniciar aplicação
    Start-ApplicationSafely -WebsiteName $config.WebsiteName -AppPoolName $config.AppPoolName
    
    # Health check
    $healthPassed = Test-ApplicationHealth -Url $healthUrl -Retries $HealthCheckRetries -DelaySeconds $HealthCheckDelay
    
    if (!$healthPassed) {
        Write-DeploymentLog "Health check falhou - iniciando rollback..." -Level Error
        
        if ($backupLocation) {
            $rollbackSuccess = Invoke-Rollback -BackupPath $backupLocation -WebsiteName $config.WebsiteName -AppPoolName $config.AppPoolName
            if ($rollbackSuccess) {
                throw "Deployment falhou, rollback executado com sucesso"
            } else {
                throw "Deployment falhou E rollback falhou - intervenção manual necessária"
            }
        } else {
            throw "Deployment falhou e backup não disponível - intervenção manual necessária"
        }
    }
    
    Write-DeploymentLog "🎉 DEPLOYMENT CONCLUÍDO COM SUCESSO!" -Level Success
    Write-DeploymentLog "Versão: $fullVersion"
    Write-DeploymentLog "URL: $healthUrl"
    Write-DeploymentLog "Path: $deploymentPath"
    
    # Limpar recursos temporários
    if ($backupLocation -and (Test-Path $backupLocation)) {
        Write-DeploymentLog "Backup mantido em: $backupLocation"
    }
    
}
catch {
    Write-DeploymentLog "❌ DEPLOYMENT FALHOU: $($_.Exception.Message)" -Level Error
    
    # Log detalhado do erro
    Write-DeploymentLog "Stack trace: $($_.ScriptStackTrace)" -Level Error
    
    exit 1
}
finally {
    Write-DeploymentLog "Deployment script finalizado em $(Get-Date)"
}