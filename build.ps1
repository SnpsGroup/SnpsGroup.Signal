[CmdletBinding()]
param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$Arguments
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildProject = Join-Path $scriptDir "src/build/_build.csproj"

& dotnet run --project $buildProject -- @Arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
