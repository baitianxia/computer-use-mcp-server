param(
    [Parameter(Mandatory)][string]$BundleRoot,
    [Parameter(Mandatory)][string]$ReportPath,
    [switch]$EnableOnHostedRunner,
    [ValidateRange(5,300)][int]$TimeoutSeconds = 90
)
$ErrorActionPreference = 'Stop'
$report = [ordered]@{
    status = 'blocked'; stage = 'preflight'; detail = ''
    scope = 'RDP connection and WTS child session ID'; rdpWtsBindingVerified = $false
    os = [Environment]::OSVersion.VersionString; powershell = $PSVersionTable.PSVersion.ToString()
    parentSessionId = $null; childSessionId = $null; existingChildSessionId = $null
    initiallyEnabled = $null; enableAttempted = $false; previewClosed = $false
    cleanup = ''; testScriptSha256 = $null; hostScriptSha256 = $null
}
$child = $null
try {
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
    $report.stage = 'child-session-support'
    $report.initiallyEnabled = Get-ChildSessionSupport
    if (-not $report.initiallyEnabled -and -not $EnableOnHostedRunner) {
        throw 'Child sessions are disabled.'
    }
    $report.stage = 'check-existing-child'
    $existing = Get-ChildSessionId -Stage before-connect
    if ($null -ne $existing -and $existing -ne [uint32]::MaxValue) {
        $report.existingChildSessionId = $existing
        throw "A child session already exists (ID $existing). The connection test requires a fresh child session."
    }
    if (-not $report.initiallyEnabled) {
        $report.stage = 'enable-on-hosted-runner'
        $report.enableAttempted = $true
        $report.cleanup = 'The disposable hosted VM and any child session will be removed at job termination.'
        Set-ChildSessionSupport -Enabled $true
    }

    $report.status = 'failed'
    $report.stage = 'connect-child'
    $child = Open-ChildSessionHost -TimeoutSeconds $TimeoutSeconds
    $report.childSessionId = $child.ChildSessionId
    $report.stage = 'verify-bound-child'
    if ($null -eq $report.childSessionId -or $report.childSessionId -le 0 -or
        $report.childSessionId -eq [uint32]::MaxValue -or $report.childSessionId -eq $report.parentSessionId -or
        $child.ParentSessionId -ne $report.parentSessionId -or -not (Test-ChildSessionHostConnected -Child $child)) {
        throw 'The RDP control and WTS did not confirm a distinct, connected child session.'
    }
    $report.rdpWtsBindingVerified = $true
    $report.status = 'passed'
    $report.detail = 'The control connected and WTS confirmed the bound child ID. Desktop logon, applications and input were not tested.'
} catch {
    $report.detail = $_.Exception.Message
} finally {
    if ($null -ne $child) {
        try {
            Close-ChildSessionHost -Child $child
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
    $target = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ReportPath)
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
    [IO.File]::WriteAllText($target, ($report | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
}
Write-Output "RDP child connection $($report.status) at $($report.stage): $($report.detail) Report: $target"
if ($report.status -eq 'passed') { exit 0 }
if ($report.status -eq 'blocked') { exit 2 }
exit 1
