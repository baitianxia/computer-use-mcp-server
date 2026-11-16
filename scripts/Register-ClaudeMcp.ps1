param(
    [ValidateSet('local','project','user')][string]$Scope = 'project',
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$server = Join-Path $repo "artifacts/$Runtime/server/WindowsComputerUse.Server.exe"
if (-not (Test-Path $server)) { throw 'Run scripts/Build.ps1 first.' }
Push-Location $repo
try {
    & claude mcp add --transport stdio --scope $Scope windows_cu -- $server
    if ($LASTEXITCODE -ne 0) { throw 'Claude Code MCP registration failed; inspect the CLI error (an existing registration may need updating).' }
    Write-Host 'Registered windows_cu. Start/restart Claude Code in this project and use /mcp to verify the connection.'
} finally { Pop-Location }
