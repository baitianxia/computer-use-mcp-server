param(
    [ValidateSet('local','project','user')][string]$Scope = 'project',
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64',
    [string]$ProjectDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $ProjectDirectory) { $ProjectDirectory = $root }
$ProjectDirectory = [IO.Path]::GetFullPath($ProjectDirectory)
if (Test-Path (Join-Path $root 'server')) { $server = Join-Path $root 'server/WindowsComputerUse.Server.exe' }
else { $server = Join-Path $root "artifacts/$Runtime/server/WindowsComputerUse.Server.exe" }
if (-not (Test-Path $server)) { throw 'Extract the full test bundle or run scripts/Build.ps1 first.' }
Push-Location $ProjectDirectory
try {
    if ($Scope -eq 'project') {
        $path = Join-Path $ProjectDirectory '.mcp.json'
        if (Test-Path $path) { $config = Get-Content $path -Raw -Encoding UTF8 | ConvertFrom-Json }
        else { $config = [pscustomobject]@{} }
        if (-not $config.PSObject.Properties['mcpServers']) { $config | Add-Member -NotePropertyName mcpServers -NotePropertyValue ([pscustomobject]@{}) }
        if ($null -eq $config.mcpServers -or $config.mcpServers -isnot [pscustomobject]) { throw 'Existing .mcp.json has an invalid mcpServers object; no changes made.' }
        $entry = [pscustomobject]@{ type = 'stdio'; command = $server; args = @() }
        $config.mcpServers | Add-Member -NotePropertyName windows_cu -NotePropertyValue $entry -Force
        $json = $config | ConvertTo-Json -Depth 40
        if (Test-Path $path) { Copy-Item $path ($path + '.bak-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')) }
        [IO.File]::WriteAllText($path, $json, [Text.UTF8Encoding]::new($false))
        Write-Host "Configured project MCP: $path (other server entries preserved)."
    } else {
        if (-not (Get-Command claude -ErrorAction SilentlyContinue)) { throw 'Claude Code CLI is required for local/user registration.' }
        & claude mcp add --transport stdio --scope $Scope windows_cu -- $server
        if ($LASTEXITCODE -ne 0) { throw 'Claude Code registration failed. Inspect any existing registration before replacing it.' }
    }
    if (Get-Command claude -ErrorAction SilentlyContinue) {
        & claude --version
        & claude mcp get windows_cu
        if ($LASTEXITCODE -ne 0) { throw 'Config was written, but Claude Code could not read windows_cu. Inspect the CLI output.' }
        Write-Host 'Claude Code configuration lookup succeeded. Use claude mcp list or /mcp for the live connection check.'
    } else {
        Write-Host 'Claude Code is not installed/on PATH. Config has been written; CLI connection is NOT verified.'
    }
    Write-Host "Start Claude Code from: $ProjectDirectory"
    Write-Host 'After project trust/MCP approval, use /mcp and /cu-demo-orders. Model-driven tasks remain separate from automated tests.'
} finally { Pop-Location }
