<#
.SYNOPSIS
    Empacota o SnpsGroup.Signal para deploy: build, publish do SseGateway e compactacao.

.DESCRIPTION
    1. Cria/limpa o diretorio .\deploy-files
    2. Obtem a versao (fullVersion) via GET da API de deploy
    3. Faz publish do projeto SnpsGroup.SseGateway para .\deploy-files\{project-name}\{fullVersion}
    4. Compacta o conteudo de .\deploy-files em snpsgroup-signal-{fullVersion}.zip
    5. Remove os arquivos publicados, mantendo apenas o .zip final

.PARAMETER BuildApiUrl
    URL da API de build (default ja preenchida).
#>
[CmdletBinding()]
param(
    [string]$BuildApiUrl = "https://b1-desktop.snpsgroup.com:9986/deploy/api/build/winov/snpsgroup.signal"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$deployDir = Join-Path $root "deploy-files"

# ----------------------------------------------------------------------------
# Helpers
# ----------------------------------------------------------------------------
function Write-Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Done([string]$msg) { Write-Host "    [OK] $msg" -ForegroundColor Green }

# Permite certificados autoassinados na chamada da API (ambiente interno)
if (-not ("TrustAllCertsPolicy" -as [type])) {
    add-type @"
    using System.Net;
    using System.Security.Cryptography.X509Certificates;
    public class TrustAllCertsPolicy : ICertificatePolicy {
        public bool CheckValidationResult(ServicePoint sp, X509Certificate cert, WebRequest req, int prob) {
            return true;
        }
    }
"@
}
[System.Net.ServicePointManager]::CertificatePolicy = New-Object TrustAllCertsPolicy
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

# ----------------------------------------------------------------------------
# 1. Preparar deploy-files
# ----------------------------------------------------------------------------
Write-Step "Preparando diretorio deploy-files"
if (Test-Path $deployDir) {
    Get-ChildItem -Path $deployDir -Force | Remove-Item -Recurse -Force
    Write-Done "Conteudo existente removido"
} else {
    New-Item -ItemType Directory -Path $deployDir | Out-Null
    Write-Done "Diretorio criado"
}

# ----------------------------------------------------------------------------
# 2. Obter fullVersion da API de build
# ----------------------------------------------------------------------------
Write-Step "Obtendo versao da API de build ($BuildApiUrl)"
try {
    $buildInfo = Invoke-RestMethod -Method Get -Uri $BuildApiUrl -ContentType "application/json"
} catch {
    Write-Host "    Falha ao chamar a API de build: $($_.Exception.Message)" -ForegroundColor Red
    throw
}

$fullVersion = $buildInfo.fullVersion
if ([string]::IsNullOrWhiteSpace($fullVersion)) {
    throw "Propriedade 'fullVersion' ausente ou vazia no retorno da API."
}
Write-Done "Versao obtida: $fullVersion"

# ----------------------------------------------------------------------------
# 3. Publish do projeto SnpsGroup.SseGateway
# ----------------------------------------------------------------------------
$projectsToPublish = @(
    "src/SnpsGroup.SseGateway/SnpsGroup.SseGateway.csproj"
)

foreach ($relProj in $projectsToPublish) {
    $projPath = Join-Path $root $relProj
    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($relProj)
    $outDir = Join-Path $deployDir "$projectName/$fullVersion"

    Write-Step "Publish $projectName -> $outDir"
    dotnet publish $projPath `
        --configuration Release `
        --output $outDir `
        --no-self-contained

    if ($LASTEXITCODE -ne 0) {
        throw "Publish do projeto $projectName falhou (exit $LASTEXITCODE)."
    }
    Write-Done "Publish concluido"
}

# ----------------------------------------------------------------------------
# 4. Compactar deploy-files
# ----------------------------------------------------------------------------
$zipName = "snpsgroup-signal-$fullVersion.zip"
$zipPath = Join-Path $deployDir $zipName

Write-Step "Compactando para $zipName"
Get-ChildItem -Path $deployDir -Directory | Compress-Archive -DestinationPath $zipPath -CompressionLevel Optimal -Force
Write-Done "Arquivo gerado: $zipName"

# ----------------------------------------------------------------------------
# 5. Limpar arquivos publicados (manter apenas o .zip)
# ----------------------------------------------------------------------------
Write-Step "Removendo arquivos publicados"
Get-ChildItem -Path $deployDir -Directory | Remove-Item -Recurse -Force
Write-Done "Limpeza concluida"

# ----------------------------------------------------------------------------
# Resultado final
# ----------------------------------------------------------------------------
Write-Host "`n==================================================" -ForegroundColor Green
Write-Host " Deploy concluido com sucesso!" -ForegroundColor Green
Write-Host " Versao: $fullVersion" -ForegroundColor Green
Write-Host " Saida:  $zipPath" -ForegroundColor Green
Write-Host "==================================================`n" -ForegroundColor Green
