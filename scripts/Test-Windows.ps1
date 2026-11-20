param(
    [ValidateSet('win-x64','win-arm64')][string]$Runtime = 'win-x64',
    [string]$BundleRoot,
    [string]$ReportDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $BundleRoot) {
    if (Test-Path (Join-Path $root 'server')) { $BundleRoot = $root }
    else { $BundleRoot = Join-Path $root "artifacts/$Runtime" }
}
$BundleRoot = [IO.Path]::GetFullPath($BundleRoot)
if (-not $ReportDirectory) { $ReportDirectory = Join-Path $BundleRoot ('test-results/' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + "-$PID") }
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
New-Item -ItemType Directory -Force $ReportDirectory | Out-Null
$results = New-Object System.Collections.Generic.List[object]
function Save-Json($path, $value) {
    [IO.File]::WriteAllText($path, ($value | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
}
function Run-Suite([string]$name, [string]$exe, [string[]]$parameters) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $exitCode = $null
    $failure = $null
    try {
        if (-not (Test-Path $exe)) { throw "Missing executable: $exe" }
        Write-Host "Running $name..."
        $oldPreference = $ErrorActionPreference
        try {
            # Windows PowerShell 5.1 treats native stderr as error records; collect all output.
            $ErrorActionPreference = 'Continue'
            & $exe @parameters 2>&1 | Tee-Object -FilePath (Join-Path $ReportDirectory "$name.log") | Out-Host
            $exitCode = $LASTEXITCODE
        } finally { $ErrorActionPreference = $oldPreference }
        if ($exitCode -ne 0) { $failure = "Process exit code $exitCode; see $name.log and suite JSON." }
    } catch { $failure = $_.Exception.Message }
    $status = 'passed'
    if ($failure) { if ($null -eq $exitCode) { $status = 'blocked' } else { $status = 'failed' } }
    $results.Add([pscustomobject]@{ name = $name; status = $status; exitCode = $exitCode; elapsedMs = $watch.ElapsedMilliseconds; error = $failure })
}

$preflight = $null
try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Requires Windows.' }
    if ([Environment]::OSVersion.Version.Build -lt 19041) { throw 'Requires Windows build 19041 or newer for this package.' }
    if (-not [Environment]::UserInteractive) { throw 'Sign in to an interactive Windows desktop before running the native suites.' }
    if (-not (Test-Path "$BundleRoot/package-manifest.json")) { throw 'Package manifest missing. Run scripts/Build.ps1 or extract a complete test bundle.' }
    $manifest = Get-Content "$BundleRoot/package-manifest.json" -Raw | ConvertFrom-Json
    foreach ($file in $manifest.files) {
        $path = Join-Path $BundleRoot $file.path
        if (-not (Test-Path $path) -or (Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) { throw "Package file missing or changed: $($file.path)" }
    }
    $env:WINDOWS_CU_SOURCE_COMMIT = $manifest.sourceCommit
    $results.Add([pscustomobject]@{ name = 'preflight'; status = 'passed'; error = $null })
} catch {
    $preflight = $_.Exception.Message
    $results.Add([pscustomobject]@{ name = 'preflight'; status = 'blocked'; error = $preflight })
    Write-Warning $preflight
}

$server = Join-Path $BundleRoot 'server/WindowsComputerUse.Server.exe'
$tester = Join-Path $BundleRoot 'tester/WindowsComputerUse.Tests.exe'
$common = @('--server', $server, '--report-dir', $ReportDirectory)
if (-not $preflight) {
    Run-Suite 'portable' $tester @()
    Run-Suite 'protocol' $tester (@('--protocol') + $common)
    Run-Suite 'winforms' $tester (@('--windows-smoke') + $common + @('--demo', (Join-Path $BundleRoot 'demo/CuDemo.exe')))
    Run-Suite 'wpf-matrix' $tester (@('--windows-matrix') + $common + @('--wpf', (Join-Path $BundleRoot 'wpf/CuWpfDemo.exe'), '--demo', (Join-Path $BundleRoot 'demo/CuDemo.exe')))
} else {
    foreach ($name in @('portable','protocol','winforms','wpf-matrix')) { $results.Add([pscustomobject]@{ name = $name; status = 'not_run'; error = 'Preflight blocked.' }) }
}

$claude = Get-Command claude -ErrorAction SilentlyContinue
$claudeVersion = $null
if ($claude) {
    try { $claudeVersion = (& claude --version 2>&1 | Out-String).Trim() } catch { $claudeVersion = $_.Exception.Message }
}
$manual = @(
    'Claude Code: run Setup-Claude.cmd, then /mcp and /windows-desktop. Verify one natural-language task in a new session.',
    'Real client: identify the app, complete one representative task, verify the requested identifier and final business data.',
    'Display coverage: multiple monitors/DPI, HDR and resizing while capture is active require the corresponding hardware/manual checks.',
    'Background coexistence: sampled focus checks do not prove uninterrupted operation while a human is using mouse/keyboard.'
)
$failed = @($results | Where-Object { $_.status -eq 'failed' }).Count
$blocked = @($results | Where-Object { $_.status -eq 'blocked' }).Count
$notRun = @($results | Where-Object { $_.status -eq 'not_run' }).Count
$passed = @($results | Where-Object { $_.status -eq 'passed' }).Count
$summary = [ordered]@{
    timestamp = [DateTime]::UtcNow.ToString('o'); sourceCommit = $env:WINDOWS_CU_SOURCE_COMMIT
    environment = @{ os = [Environment]::OSVersion.VersionString; processArchitecture = $env:PROCESSOR_ARCHITECTURE; sessionId = (Get-Process -Id $PID).SessionId; userInteractive = [Environment]::UserInteractive; powerShell = $PSVersionTable.PSVersion.ToString(); claudeVersion = $claudeVersion }
    automated = @{ total = $results.Count; passed = $passed; failed = $failed; blocked = $blocked; notRun = $notRun; results = @($results.ToArray()) }
    claudeModelVerified = $false; realClientVerified = $false; manualNotRun = $manual
}
Save-Json (Join-Path $ReportDirectory 'summary.json') $summary
$lines = @('# Windows Computer Use test report', '', "Source: $($summary.sourceCommit)", '', "Automated suites: $passed passed, $failed failed, $blocked blocked, $notRun not run.", '', '| Suite | Status | Error |', '|---|---|---|')
foreach ($result in $results) { $lines += "| $($result.name) | $($result.status) | $($result.error) |" }
$lines += @('', 'Manual checks still required:', '')
foreach ($item in $manual) { $lines += "- $item" }
[IO.File]::WriteAllLines((Join-Path $ReportDirectory 'REPORT.md'), $lines, [Text.UTF8Encoding]::new($false))
Add-Type -AssemblyName System.IO.Compression.FileSystem
$reportZip = $ReportDirectory + '.zip'
if (Test-Path $reportZip) { Remove-Item $reportZip }
[IO.Compression.ZipFile]::CreateFromDirectory($ReportDirectory, $reportZip)
Write-Host "Report: $ReportDirectory"
Write-Host "Share this result archive: $reportZip"
Write-Host "Automated: $passed passed; $failed failed; $blocked blocked; $notRun not run. Manual Claude/client checks are separate."
if ($failed -gt 0 -or $blocked -gt 0 -or $notRun -gt 0) { exit 1 }
exit 0
