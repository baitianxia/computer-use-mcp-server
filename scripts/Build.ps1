param([ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    & dotnet build WindowsComputerUse.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & dotnet run --project tests/WindowsComputerUse.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Portable tests failed.' }
    $projects = @{
        server = 'src/WindowsComputerUse.Server/WindowsComputerUse.Server.csproj'
        worker = 'src/WindowsComputerUse.Worker/WindowsComputerUse.Worker.csproj'
        demo = 'samples/CuDemo/CuDemo.csproj'
    }
    foreach ($part in $projects.Keys) {
        & dotnet publish $projects[$part] -c Release -r $Runtime --self-contained true -o "artifacts/$Runtime/$part" /p:PublishSingleFile=false
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $part" }
    }
    $server = Join-Path $repo "artifacts/$Runtime/server/WindowsComputerUse.Server.exe"
    $config = @{ mcpServers = @{ windows_cu = @{ type = 'stdio'; command = $server; args = @() } } }
    $json = $config | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText((Join-Path $repo "artifacts/$Runtime/claude-mcp.json"), $json, [System.Text.UTF8Encoding]::new($false))
    Write-Host "Published to artifacts/$Runtime. Windows desktop/Claude Code verification has not been run by this build script."
} finally { Pop-Location }
