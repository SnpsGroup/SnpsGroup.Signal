param(
    [ValidateSet("full", "load", "soak")]
    [string]$Mode = "load",
    [string]$Output = "artifacts/load-tests"
)

$ErrorActionPreference = "Stop"

Write-Host "Running DFE load tests in mode '$Mode'..."
dotnet run --project tests/SnpsGroup.Dfe.LoadTests/SnpsGroup.Dfe.LoadTests.csproj -- --mode $Mode --output $Output
