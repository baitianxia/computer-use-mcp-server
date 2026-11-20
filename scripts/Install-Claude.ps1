param(
    [string]$BundleRoot,
    [string]$InstallRoot,
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64'
)
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Installation requires Windows.' }
if (-not (Get-Command claude -ErrorAction SilentlyContinue)) { throw 'Install Claude Code first and confirm claude --version works in this terminal.' }
$root = Split-Path -Parent $PSScriptRoot
if (-not $BundleRoot) {
    if (Test-Path (Join-Path $root 'package-manifest.json')) { $BundleRoot = $root }
    else { $BundleRoot = Join-Path $root "artifacts/$Runtime" }
}
$BundleRoot = [IO.Path]::GetFullPath($BundleRoot)
if (-not $InstallRoot) { $InstallRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'WindowsComputerUse' }
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$claudeDirectory = $env:CLAUDE_CONFIG_DIR
if (-not $claudeDirectory) { $claudeDirectory = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.claude' }
$claudeDirectory = [IO.Path]::GetFullPath($claudeDirectory)
$manifestPath = Join-Path $BundleRoot 'package-manifest.json'
if (-not (Test-Path $manifestPath)) { throw 'Extract the complete Windows test package or run scripts/Build.ps1 first.' }
$manifest = Get-Content $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$manifestHash = (Get-FileHash $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
$version = $manifestHash.Substring(0, 16)
if ($manifest.sourceCommit -match '^[a-fA-F0-9]{40}$') { $version = $manifest.sourceCommit.Substring(0, 12) + '-' + $version }
$destination = Join-Path $InstallRoot "versions/$version"
$backup = Join-Path $InstallRoot ('backups/' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
function Save-Json([string]$path, $data) {
    [IO.File]::WriteAllText($path, ($data | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
}
function Package-File([string]$base, [string]$relative) {
    $full = [IO.Path]::GetFullPath((Join-Path $base $relative))
    if (-not $full.StartsWith($base.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Invalid package path: $relative" }
    return $full
}
function Verify-Package([string]$base) {
    foreach ($file in $manifest.files) {
        $path = Package-File $base $file.path
        if (-not (Test-Path $path) -or (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) { throw "Package verification failed: $($file.path)" }
    }
}
function Claude-Command([string[]]$parameters) {
    $oldPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $output = (& claude @parameters 2>&1 | Out-String)
        return [pscustomobject]@{ code = $LASTEXITCODE; output = $output }
    } finally { $ErrorActionPreference = $oldPreference }
}

Verify-Package $BundleRoot
New-Item -ItemType Directory -Force $InstallRoot, (Join-Path $InstallRoot 'versions') | Out-Null
if (Test-Path $destination) { Verify-Package $destination }
else {
    $stage = Join-Path $InstallRoot ('.install-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $stage | Out-Null
    try {
        foreach ($file in $manifest.files) {
            $target = Package-File $stage $file.path
            New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
            Copy-Item -LiteralPath (Package-File $BundleRoot $file.path) -Destination $target
        }
        Copy-Item $manifestPath (Join-Path $stage 'package-manifest.json')
        Verify-Package $stage
        Move-Item -LiteralPath $stage -Destination $destination
    } finally { if (Test-Path $stage) { Remove-Item $stage -Recurse -Force } }
}
$server = Join-Path $destination 'server/WindowsComputerUse.Server.exe'
Push-Location $InstallRoot
try {
    $added = Claude-Command -parameters @('mcp','add','--transport','stdio','--scope','user','windows_cu','--',$server)
    if ($added.code -ne 0 -and $added.output -match 'already exists') {
        New-Item -ItemType Directory -Force $backup | Out-Null
        $previous = Claude-Command -parameters @('mcp','get','windows_cu')
        [IO.File]::WriteAllText((Join-Path $backup 'previous-mcp.txt'), $previous.output, [Text.UTF8Encoding]::new($false))
        $removed = Claude-Command -parameters @('mcp','remove','--scope','user','windows_cu')
        if ($removed.code -ne 0) { throw "Cannot update user MCP registration: $($removed.output)" }
        $added = Claude-Command -parameters @('mcp','add','--transport','stdio','--scope','user','windows_cu','--',$server)
    }
    if ($added.code -ne 0) { throw "Claude Code registration failed: $($added.output). Installed program files remain at $destination." }

    $skillRoot = Join-Path $claudeDirectory 'skills'
    New-Item -ItemType Directory -Force $skillRoot | Out-Null
    foreach ($name in @('windows-desktop','cu-demo-orders')) {
        $source = Join-Path $destination ".claude/skills/$name"
        $target = Join-Path $skillRoot $name
        $skillStage = Join-Path $InstallRoot ('.skill-' + [Guid]::NewGuid().ToString('N'))
        try {
            Copy-Item -LiteralPath $source -Destination $skillStage -Recurse
            Save-Json (Join-Path $skillStage 'installation.json') @{ installDirectory = $destination; sourceCommit = $manifest.sourceCommit }
            if (Test-Path $target) {
                New-Item -ItemType Directory -Force (Join-Path $backup 'skills') | Out-Null
                Move-Item -LiteralPath $target -Destination (Join-Path $backup "skills/$name")
            }
            Move-Item -LiteralPath $skillStage -Destination $target
        } finally { if (Test-Path $skillStage) { Remove-Item $skillStage -Recurse -Force } }
    }
    $health = Claude-Command -parameters @('mcp','get','windows_cu')
    $connected = $health.code -eq 0 -and $health.output -match 'Connected' -and $health.output -notmatch 'Failed to connect|Pending approval'
    $receipt = @{ installedAt = [DateTime]::UtcNow.ToString('o'); sourceCommit = $manifest.sourceCommit; installDirectory = $destination;
        server = $server; mcpScope = 'user'; skillsDirectory = $skillRoot; skills = @('windows-desktop','cu-demo-orders');
        cliConnected = $connected; modelTaskVerified = $false; backups = $backup }
    Save-Json (Join-Path $InstallRoot 'installation.json') $receipt
    Write-Host "Installed program: $destination"
    Write-Host "Installed personal Skills: $skillRoot"
    Write-Host 'Registered windows_cu in Claude Code user scope.'
    Write-Host 'Start or restart Claude Code in your usual working directory. The extracted package directory is no longer required.'
    if (-not $connected) { throw "Installation completed, but MCP connection is not verified. Run claude mcp get windows_cu to inspect the issue. Receipt: $InstallRoot/installation.json" }
    Write-Host 'Claude Code MCP connection verified. Model-driven tasks still need a normal Claude Code session.'
} finally { Pop-Location }
