# Script de configuração IIS com automação completa de certificados SSL
# Suporte a certificados wildcard com download automático via API

function Initialize-SnpsGroupIIS {
    param(
        [Parameter(Mandatory=$true)]
        [ValidateSet("Production", "Staging")]
        [string]$Environment,
        
        [Parameter(Mandatory=$true)]
        [string]$Version,
        
        [string]$CloudProvider = "winov",
        [string]$Repository = "SnpsGroup.Dfe",
        
        # Parâmetros para automação de certificados
        [Parameter(Mandatory=$true)]
        [string]$VersionApiUrl,
        
        [Parameter(Mandatory=$true)]
        [SecureString]$CertificatePassword
    )
    
    # Configurações específicas por ambiente
    $config = @{
        Production = @{
            WebsiteName = "AdaptiveDfe"
            AppPoolName = "AdaptiveDfe" 
            DomainName = "def.snpsgroup.com"
            VersionSuffix = "400"
        }
        Staging = @{
            WebsiteName = "StageAdaptiveDfe"
            AppPoolName = "StageAdaptiveDfe"
            DomainName = "stage-dfe.snpsgroup.com"  
            VersionSuffix = "200"
        }
    }
    
    # Seleciona a instância com as informações para o ambiente
    $envConfig = $config[$Environment]
    $fullVersion = "$Version.$($envConfig.VersionSuffix)"
    $physicalPath = "C:\AzureDevops\$Environment\$Repository\$fullVersion"
    
    Import-Module WebAdministration
    
    try {
        Write-Host "🔧 Configurando IIS para $Environment..."
        
        # ====
        # STEP 1: CONFIGURAÇÃO BÁSICA DO IIS
        # ====
        
        # Criar Application Pool
        if (!(Get-IISAppPool -Name $envConfig.AppPoolName -ErrorAction SilentlyContinue)) {
            New-WebAppPool -Name $envConfig.AppPoolName
            Write-Host "✓ Application Pool '$($envConfig.AppPoolName)' criado"
        }
        
        # Configurar Application Pool para .NET Core
        Set-ItemProperty -Path "IIS:\AppPools\$($envConfig.AppPoolName)" -name "managedRuntimeVersion" -value ""
        Set-ItemProperty -Path "IIS:\AppPools\$($envConfig.AppPoolName)" -name "processModel" -value @{identitytype="ApplicationPoolIdentity"}
        
        # Criar diretório físico
        if (!(Test-Path $physicalPath)) {
            New-Item -Path $physicalPath -ItemType Directory -Force
            Write-Host "✓ Diretório criado: $physicalPath"
        }
        
        # Criar ou atualizar Website
        if (!(Get-Website -Name $envConfig.WebsiteName -ErrorAction SilentlyContinue)) {
            New-Website -Name $envConfig.WebsiteName -PhysicalPath $physicalPath -ApplicationPool $envConfig.AppPoolName -Port 80
            Write-Host "✓ Website '$($envConfig.WebsiteName)' criado"
        } else {
            Set-ItemProperty -Path "IIS:\Sites\$($envConfig.WebsiteName)" -name "physicalPath" -value $physicalPath
            Write-Host "✓ Website atualizado para nova versão"
        }
        
        # ====
        # STEP 2: CONFIGURAÇÃO AUTOMÁTICA DE CERTIFICADO SSL
        # ====
        
        Write-Host "🔐 Configurando certificado SSL..."
        
        # 1. Verificar se já existe certificado wildcard válido
        $domain = $envConfig.DomainName
        $wildcardDomain = "*." + ($domain -replace "^[^.]+\.", "") # Ex: *.snpsgroup.com
        
        Write-Host "Procurando certificado wildcard para: $wildcardDomain"
        
        $cert = Get-ChildItem Cert:\LocalMachine\WebHosting | Where-Object {
            ($_.Subject -like "CN=$wildcardDomain*" -or $_.DnsNameList.Unicode -contains $wildcardDomain) -and 
            $_.NotAfter -gt (Get-Date).AddDays(5) # Válido por pelo menos 5 dias
        } | Sort-Object NotAfter -Descending | Select-Object -First 1
        
        if ($cert) {
            Write-Host "✓ Certificado wildcard já instalado: $($cert.Subject)"
            Write-Host "  Válido até: $($cert.NotAfter)"
            Write-Host "  Thumbprint: $($cert.Thumbprint)"
            $certThumbprint = $cert.Thumbprint
        } else {
            Write-Host "🔎 Nenhum certificado wildcard válido encontrado. Buscando na API..."
            
            # 3. Baixar certificado da API
            $baseDomain = ($domain -replace "^[^.]+\.", "") # Remove subdomínio: snpsgroup.com
            $apiUrl = "$VersionApiUrl/api/$baseDomain/certificate"
            
            Write-Host "Fazendo requisição para: $apiUrl"
            
            try {
                # Fazer requisição para a API
                $response = Invoke-RestMethod -Uri $apiUrl -Method Get -ContentType "application/octet-stream"
                
                if ($response -and $response.Length -gt 0) {
                    Write-Host "✓ Certificado baixado da API ($($response.Length) bytes)"
                    
                    # 4. Salvar e importar certificado
                    $tempDir = "C:\Temp"
                    if (!(Test-Path $tempDir)) {
                        New-Item -Path $tempDir -ItemType Directory -Force | Out-Null
                    }
                    
                    $pfxPath = Join-Path $tempDir "$($baseDomain)_wildcard.pfx"
                    
                    # Salvar bytes como arquivo PFX
                    [System.IO.File]::WriteAllBytes($pfxPath, $response)
                    Write-Host "✓ Certificado salvo em: $pfxPath"
                    
                    # Importar certificado no repositório Local Machine
                    $importedCert = Import-PfxCertificate -FilePath $pfxPath -CertStoreLocation Cert:\LocalMachine\WebHosting -Password $CertificatePassword
                    
                    if ($importedCert) {
                        Write-Host "✓ Certificado importado com sucesso"
                        Write-Host "  Subject: $($importedCert.Subject)"
                        Write-Host "  Válido até: $($importedCert.NotAfter)"
                        Write-Host "  Thumbprint: $($importedCert.Thumbprint)"
                        $certThumbprint = $importedCert.Thumbprint
                        
                        # Limpar arquivo temporário
                        Remove-Item $pfxPath -Force -ErrorAction SilentlyContinue
                        Write-Host "✓ Arquivo temporário removido"
                    } else {
                        throw "Falha ao importar o certificado"
                    }
                } else {
                    throw "API retornou resposta vazia ou inválida"
                }
            }
            catch {
                Write-Error "❌ Erro ao baixar/importar certificado: $($_.Exception.Message)"
                throw "Falha na configuração do certificado SSL: $($_.Exception.Message)"
            }
        }
        
        # ====
        # STEP 3: CONFIGURAR BINDING HTTPS COM CERTIFICADO
        # ====
        
        Write-Host "🔗 Configurando binding HTTPS..."

        Import-Module IISAdministration
        
        try {

            if (-not (Get-IISSiteBinding -Name $envConfig.WebsiteName -BindingInformation "*:443:$domain" -Protocol https -ErrorAction SilentlyContinue)) {
                New-IISSiteBinding `
                -Name $envConfig.WebsiteName `
                -BindingInformation "*:443:$domain" `
                -Protocol https `
                -SslFlag Sni `
                -CertificateThumbPrint $certThumbprint `
                -CertStoreLocation "Cert:\LocalMachine\WebHosting"
            }
            else {
                # Atualiza o certificado sem remover nada
                # Módulo já importado no início do script
                # Import-Module WebAdministration
                (Get-WebBinding -Name $envConfig.WebsiteName -Protocol https -Port 443 -HostHeader $domain).
                    AddSslCertificate($certThumbprint, "WebHosting")
            }

            Write-Host "✅ Binding HTTPS criado/atualizado com sucesso!"
            Write-Host "   Site: $($envConfig.WebsiteName)"
            Write-Host "   Domínio: $domain"
            Write-Host "   Thumbprint: $certThumbprint"
        } catch {
            Write-Error "Erro ao configurar binding HTTPS: $_"
        }

# ====
# STEP 4: CONFIGURAÇÕES FINAIS
# ====

# Configurar autenticação anônima
        Set-WebConfigurationProperty -Filter "/system.webServer/security/authentication/anonymousAuthentication" -Name "enabled" -Value $true -Location $envConfig.WebsiteName -PSPath IIS:\
        
        # Reiniciar Application Pool
        Restart-WebAppPool -Name $envConfig.AppPoolName
        Write-Host "✓ Application Pool reiniciado"
        
        # Teste básico de conectividade HTTPS
        Write-Host "🧪 Testando conectividade HTTPS..."
        try {
            $testUrl = "https://$domain"
            $testResult = Test-NetConnection -ComputerName $domain -Port 443 -InformationLevel Quiet -WarningAction SilentlyContinue
            if ($testResult) {
                Write-Host "✅ Conectividade HTTPS OK"
            } else {
                Write-Warning "⚠️ Teste de conectividade HTTPS falhou - pode ser normal se DNS não estiver propagado"
            }
        }
        catch {
            Write-Warning "⚠️ Não foi possível testar conectividade HTTPS: $($_.Exception.Message)"
        }
        
        return @{
            Success = $true
            PhysicalPath = $physicalPath
            FullVersion = $fullVersion
            WebsiteName = $envConfig.WebsiteName
            DomainName = $domain
            CertificateThumbprint = $certThumbprint
            HttpsConfigured = $true
        }
    }
    catch {
        Write-Error "❌ Erro na configuração IIS: $($_.Exception.Message)"
        return @{ 
            Success = $false
            Error = $_.Exception.Message
            PhysicalPath = $physicalPath
            WebsiteName = $envConfig.WebsiteName
        }
    }
}

# Função auxiliar para validar certificado
function Test-CertificateValidity {
    param(
        [Parameter(Mandatory=$true)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,
        
        [string]$Domain,
        [int]$MinimumDaysValid = 30
    )
    
    $now = Get-Date
    $expiryDate = $Certificate.NotAfter
    $daysUntilExpiry = ($expiryDate - $now).Days
    
    $isValid = $Certificate.NotBefore -le $now -and $expiryDate -gt $now.AddDays($MinimumDaysValid)
    
    return @{
        IsValid = $isValid
        DaysUntilExpiry = $daysUntilExpiry
        NotBefore = $Certificate.NotBefore
        NotAfter = $Certificate.NotAfter
        Subject = $Certificate.Subject
        Thumbprint = $Certificate.Thumbprint
    }
}