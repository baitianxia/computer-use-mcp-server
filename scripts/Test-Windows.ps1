param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    $server = Join-Path $repo "artifacts/$Runtime/server/WindowsComputerUse.Server.exe"
    $demo = Join-Path $repo "artifacts/$Runtime/demo/CuDemo.exe"
    if (-not (Test-Path $server) -or -not (Test-Path $demo)) { throw 'Run scripts/Build.ps1 first.' }
    & dotnet run --project tests/WindowsComputerUse.Tests -c Release --no-build -- --windows-smoke --server $server --demo $demo --report-dir artifacts/acceptance
    if ($LASTEXITCODE -ne 0) { throw 'Windows smoke test failed or could not run. See artifacts/acceptance/windows-smoke.json.' }
    Write-Host 'Native demo smoke test passed. Claude Code /mcp, Skills, vision and a real business client still need separate acceptance.'
} finally { Pop-Location }
