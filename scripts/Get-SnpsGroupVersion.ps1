param(
    [string]$CloudProvider = "winov",
    [string]$Repository = "SnpsGroup.Signal",
    [string]$ApiBaseUrl = "https://b1-desktop.snpsgroup.com:9986/deploy/api",
    [string]$VariableGroupName = "SnpsGroup.Global",
    [string]$FallbackVarName = "fallbackVersion"
)

$apiUrl = "$ApiBaseUrl/build/$CloudProvider/$Repository"

function Get-AdoAuthHeader {
    $token = $env:SYSTEM_ACCESSTOKEN
    if (-not $token) { throw "SYSTEM_ACCESSTOKEN ausente. Habilite 'Allow scripts to access OAuth token' no job." }
    @{ Authorization = "Bearer $token" }
}

function Get-VariableGroupByName([string]$orgUrl, [string]$project, [string]$name) {
    $u = "$($orgUrl.TrimEnd('/'))/$project/_apis/distributedtask/variablegroups?groupName=$([uri]::EscapeDataString($name))&api-version=7.1"
    (Invoke-RestMethod -Headers (Get-AdoAuthHeader) -Uri $u -Method Get).value | Select-Object -First 1
}

function Set-VariableGroupValue([int]$groupId, [hashtable]$vgBody, [string]$orgUrl) {
    $u = "$($orgUrl.TrimEnd('/'))/_apis/distributedtask/variablegroups/$groupId?api-version=7.1"
    $json = $vgBody | ConvertTo-Json -Depth 100
    Invoke-RestMethod -Headers (Get-AdoAuthHeader) -Uri $u -Method Put -ContentType "application/json" -Body $json | Out-Null
}

function Increment-Version([string]$v) {
    if ([string]::IsNullOrWhiteSpace($v)) { return "1.0.0" }
    if ($v -notmatch '^\d+(\.\d+)*$') { return "$v.1" }
    $parts = $v.Split('.')
    for ($i = $parts.Length-1; $i -ge 0; $i--) {
        if ($parts[$i] -match '^\d+$') { $parts[$i] = ([int]$parts[$i] + 1).ToString(); break }
    }
    ($parts -join '.')
}

try {
    Write-Host "🔍 Consultando versão em: $apiUrl"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $headers = @{
        'Content-Type' = 'application/json'
        'User-Agent'   = 'SnpsGroup-AzureDevOps/1.0'
    }

    $response = Invoke-RestMethod -Uri $apiUrl -Method GET -Headers $headers -TimeoutSec 20
    if ($null -ne $response -and $response.FullVersion) {
        Write-Host "✅ Versão da API: $($response.FullVersion)"
        Write-Host "##vso[task.setvariable variable=FullVersion;isOutput=true]$($response.FullVersion)"
        if ($response.CiType) { Write-Host "##vso[task.setvariable variable=CiType;isOutput=true]$($response.CiType)" }
        Write-Host "##vso[task.setvariable variable=GitTag;isOutput=true]v$($response.FullVersion)"
        Write-Host "##vso[task.setvariable variable=StagingVersion;isOutput=true]$($response.FullVersion).200"
        Write-Host "##vso[task.setvariable variable=ProductionVersion;isOutput=true]$($response.FullVersion).400"
    }
    else { throw "API não retornou FullVersion" }
}
catch {
    Write-Warning "⚠️ Erro na API: $($_.Exception.Message)"

    $orgUrl  = $env:AZDO_ORGURL     # ex.: https://dev.azure.com/<org>/
    $project = $env:AZDO_PROJECT     # nome do projeto
    $vgName  = if ($env:VG_NAME) { $env:VG_NAME } else { "SnpsGroup.Global" }
    $varName = if ($env:FALLBACK_VAR_NAME) { $env:FALLBACK_VAR_NAME } else { "fallbackVersion" }

    function Get-AdoAuthHeader {
        $token = $env:SYSTEM_ACCESSTOKEN
        if (-not $token) { throw "SYSTEM_ACCESSTOKEN ausente. Habilite 'Allow scripts to access OAuth token' no job." }
        @{ Authorization = "Bearer $token" }
    }

    function Get-VariableGroupByName([string]$orgUrl, [string]$project, [string]$name) {
        $u = "$($orgUrl.TrimEnd('/'))/$project/_apis/distributedtask/variablegroups?groupName=$([uri]::EscapeDataString($name))&api-version=7.1"
        (Invoke-RestMethod -Headers (Get-AdoAuthHeader) -Uri $u -Method Get -TimeoutSec 30).value | Select-Object -First 1
    }

    function Increment-Version([string]$v) {
        if ([string]::IsNullOrWhiteSpace($v)) { return "1.0.0" }
        if ($v -notmatch '^\d+(\.\d+)*$') { return "$v.1" }
        $parts = $v.Split('.')
        for ($i = $parts.Length-1; $i -ge 0; $i--) {
            if ($parts[$i] -match '^\d+$') { $parts[$i] = ([int]$parts[$i] + 1).ToString(); break }
        }
        ($parts -join '.')
    }

    try {
        # 1) Ler o Variable Group e materializar 'variables' como hashtable
        $vg = Get-VariableGroupByName -orgUrl $orgUrl -project $project -name $vgName
        if (-not $vg) { throw "Variable Group '$vgName' não encontrado (projeto: $project)." }

        $vars = @{}
        if ($vg.variables) {
            foreach ($p in $vg.variables.PSObject.Properties) { $vars[$p.Name] = $p.Value }
        }

        # 2) Pegar valor atual do fallback
        $current = $null
        if ($vars.ContainsKey($varName) -and $vars[$varName]) {
            # padrão da API: @{ value = "..."; isSecret = <bool> }
            $current = $vars[$varName].value
        }
        if (-not $current) {
            $current = "1.0.$(Get-Date -Format 'yyyyMMdd')"  # fallback emergencial se não existir
        }

        # 3) Usar o valor atual como versão do build
        $used = $current
        Write-Host "🔄 Usando fallback da Library '$vgName' → $varName=$used"
        Write-Host "##vso[task.setvariable variable=FullVersion;isOutput=true]$used"
        Write-Host "##vso[task.setvariable variable=GitTag;isOutput=true]v$used"

        # 4) Incrementar e persistir para a PRÓXIMA execução
        $next = Increment-Version $current
        $vars[$varName] = @{ value = $next; isSecret = $false }

        $body = @{
            id   = $vg.id
            name = $vg.name
            type = $vg.type
            description = $vg.description
            variableGroupProjectReferences = $vg.variableGroupProjectReferences
            variables = $vars
        }

        $putUrl = "$($orgUrl.TrimEnd('/'))/_apis/distributedtask/variablegroups/$($vg.id)?api-version=7.1"
        $json = $body | ConvertTo-Json -Depth 100
        Invoke-RestMethod -Headers (Get-AdoAuthHeader) -Uri $putUrl -Method Put -ContentType "application/json" -Body $json | Out-Null
        Write-Host "⬆️  $varName atualizado em '$vgName': $current → $next"
    }
    catch {
        Write-Warning "Não foi possível ler/atualizar o Variable Group '$vgName': $($_.Exception.Message)"
        $fallbackVersion = "1.0.0"
        Write-Host "🔁 Emergência: usando $fallbackVersion"
        Write-Host "##vso[task.setvariable variable=FullVersion;isOutput=true]$fallbackVersion"
        Write-Host "##vso[task.setvariable variable=GitTag;isOutput=true]v$fallbackVersion"
    }
}
