param(
    [Parameter(Mandatory)][string]$BundleRoot,
    [Parameter(Mandatory)][string]$ReportPath,
    [switch]$EnableOnHostedRunner,
    [ValidateRange(5,300)][int]$TimeoutSeconds = 90
)
$ErrorActionPreference = 'Stop'
$target = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ReportPath)
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
$report = [ordered]@{
    status = 'blocked'; stage = 'preflight'; detail = ''
    startedUtc = [DateTime]::UtcNow.ToString('o')
    scope = 'RDP connection and WTS child session ID'; rdpWtsBindingVerified = $false
    os = [Environment]::OSVersion.VersionString; powershell = $PSVersionTable.PSVersion.ToString()
    hostOperation = $null; lastHostObservation = $null
    parentSessionId = $null; childSessionId = $null; existingChildSessionId = $null
    initiallyEnabled = $null; enableAttempted = $false; previewClosed = $false
    cleanup = ''; testScriptSha256 = $null; hostScriptSha256 = $null
}

function Write-ConnectionReport([string]$RunningStage) {
    $snapshot = [ordered]@{} + $report
    $snapshot.updatedUtc = [DateTime]::UtcNow.ToString('o')
    if ($RunningStage) {
        $snapshot.status = 'in_progress'
        $snapshot.stage = $RunningStage
    }
    # Keep the last complete JSON if a native call hangs or the process is killed.
    $temp = "$target.$PID.tmp"
    try {
        [IO.File]::WriteAllText($temp, ($snapshot | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        if ([IO.File]::Exists($target)) {
            [IO.File]::Replace($temp, $target, [System.Management.Automation.Language.NullString]::Value)
        } else {
            [IO.File]::Move($temp, $target)
        }
    } finally {
        if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) }
    }
    if ($RunningStage) {
        Write-Host "RDP child connection in_progress at $RunningStage [$($snapshot.hostOperation)] ($($snapshot.updatedUtc))."
    }
}

function Start-ConnectionStage([string]$Stage) {
    $report.stage = $Stage
    $report.hostOperation = $null
    Write-ConnectionReport $Stage
}

$child = $null
$closingPreview = $false
$traceOptions = @{}
try {
    Start-ConnectionStage 'preflight'
    $report.testScriptSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    $hostScript = Join-Path $BundleRoot 'scripts/ChildSessionHost.ps1'
    $report.hostScriptSha256 = (Get-FileHash -LiteralPath $hostScript -Algorithm SHA256).Hash
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or
        $PSVersionTable.PSEdition -ne 'Desktop' -or $PSVersionTable.PSVersion -lt [version]'5.1' -or
        [Threading.Thread]::CurrentThread.GetApartmentState() -ne [Threading.ApartmentState]::STA) {
        throw 'Requires Windows PowerShell 5.1 with -STA.'
    }
    $report.parentSessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    if (-not [Environment]::UserInteractive -or $report.parentSessionId -le 0) {
        throw 'Requires an interactive Windows user session.'
    }
    if ($EnableOnHostedRunner -and ($env:GITHUB_ACTIONS -cne 'true' -or
        $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or $env:RUNNER_OS -cne 'Windows')) {
        throw 'Automatic enablement is limited to a disposable GitHub-hosted Windows runner.'
    }

    . $hostScript
    if ((Get-Command Open-ChildSessionHost).Parameters.ContainsKey('OnStage')) {
        $traceOptions.OnStage = {
            param([string]$Operation, [string]$Observation)
            $report.hostOperation = $Operation
            if ($Observation) { $report.lastHostObservation = $Observation }
            if ($closingPreview) { Write-ConnectionReport 'close-preview' }
            else { Write-ConnectionReport $report.stage }
        }
    }
    Start-ConnectionStage 'child-session-support'
    $report.initiallyEnabled = Get-ChildSessionSupport
    if (-not $report.initiallyEnabled -and -not $EnableOnHostedRunner) {
        throw 'Child sessions are disabled.'
    }
    Start-ConnectionStage 'check-existing-child'
    $existing = Get-ChildSessionId -Stage before-connect
    if ($null -ne $existing -and $existing -ne [uint32]::MaxValue) {
        $report.existingChildSessionId = $existing
        throw "A child session already exists (ID $existing). The connection test requires a fresh child session."
    }
    if (-not $report.initiallyEnabled) {
        $report.enableAttempted = $true
        $report.cleanup = 'The disposable hosted VM and any child session will be removed at job termination.'
        Start-ConnectionStage 'enable-on-hosted-runner'
        Set-ChildSessionSupport -Enabled $true
    }

    $report.status = 'failed'
    Start-ConnectionStage 'connect-child'
    $child = Open-ChildSessionHost -TimeoutSeconds $TimeoutSeconds @traceOptions
    $report.childSessionId = $child.ChildSessionId
    Start-ConnectionStage 'verify-bound-child'
    if ($null -eq $report.childSessionId -or $report.childSessionId -le 0 -or
        $report.childSessionId -eq [uint32]::MaxValue -or $report.childSessionId -eq $report.parentSessionId -or
        $child.ParentSessionId -ne $report.parentSessionId -or -not (Test-ChildSessionHostConnected -Child $child @traceOptions)) {
        throw 'The RDP control and WTS did not confirm a distinct, connected child session.'
    }
    $report.rdpWtsBindingVerified = $true
    $report.status = 'passed'
    $report.detail = 'The control connected and WTS confirmed the bound child ID. Desktop logon, applications and input were not tested.'
} catch {
    $report.detail = $_.Exception.Message
} finally {
    if ($null -ne $child) {
        $closingPreview = $true
        $report.hostOperation = $null
        try {
            try { Write-ConnectionReport 'close-preview' }
            finally { Close-ChildSessionHost -Child $child @traceOptions }
            if (-not $child.Form.IsDisposed) { throw 'The RDP preview was not disposed.' }
            $report.previewClosed = $true
            if (-not $report.cleanup) { $report.cleanup = 'Preview closed. The test did not log off the child session.' }
        } catch {
            $report.cleanup = "Closing the test preview failed: $($_.Exception.Message) $($report.cleanup)".Trim()
            if ($report.status -eq 'passed') {
                $report.status = 'failed'
                $report.stage = 'close-preview'
                $report.detail = $report.cleanup
            }
        }
    }
    Write-ConnectionReport
}
Write-Output "RDP child connection $($report.status) at $($report.stage): $($report.detail) Report: $target"
if ($report.status -eq 'passed') { exit 0 }
if ($report.status -eq 'blocked') { exit 2 }
exit 1
