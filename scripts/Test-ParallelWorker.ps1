# Runs in the connected child Windows session. A mistaken launch from the main desktop
# must fail before publishing any handshake file or starting a test process.
param(
    [Parameter(Mandatory)][string]$RunDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{32}$')][string]$RunId,
    [string]$BundleRoot = (Split-Path -Parent $PSScriptRoot)
)
$ErrorActionPreference = 'Stop'
$RunDirectory = (Get-Item -LiteralPath $RunDirectory).FullName
$BundleRoot = (Get-Item -LiteralPath $BundleRoot).FullName
$encoding = [Text.UTF8Encoding]::new($false, $true)

function Read-RunFile([string]$name) {
    $path = Join-Path $RunDirectory $name
    $data = [IO.File]::ReadAllText($path, $encoding) | ConvertFrom-Json
    if ($data.runId -cne $RunId) { throw "Run ID mismatch in $name." }
    return $data
}

function Write-RunFile([string]$name, $fields) {
    $data = [ordered]@{
        runId = $RunId; parentSessionId = $parentSession; childSessionId = $childSession
        childProcessId = $PID
    }
    foreach ($key in $fields.Keys) { $data[$key] = $fields[$key] }
    $path = Join-Path $RunDirectory $name
    $temp = "$path.$PID.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temp, ($data | ConvertTo-Json -Depth 12), $encoding)
        [IO.File]::Move($temp, $path)
    } finally {
        if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) }
    }
}

function Assert-ParentActive {
    if ($parentProcess.HasExited) { throw 'The parent test process has exited.' }
    if ([IO.File]::Exists((Join-Path $RunDirectory 'parent-cancel.json'))) {
        throw 'The main desktop cancelled this run.'
    }
}

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or -not [Environment]::UserInteractive) {
    throw 'This test needs an interactive Windows child session.'
}
$descriptor = Read-RunFile 'parent-session.json'
$parentSession = [int]$descriptor.parentSessionId
$childSession = [Diagnostics.Process]::GetCurrentProcess().SessionId
if ($descriptor.machineName -ne [Environment]::MachineName -or $parentSession -le 0 -or
    $childSession -le 0 -or $childSession -eq $parentSession -or $childSession -ne $descriptor.childSessionId) {
    throw "Wrong desktop for run $RunId. Open the launcher inside its child session (expected $($descriptor.childSessionId), current $childSession)."
}
$parentProcess = [Diagnostics.Process]::GetProcessById([int]$descriptor.parentProcessId)
$ownsRun = $false
$probe = $null
$outFile = $null
$errFile = $null
$logTasks = @()
$started = $null
$finished = $null
$inputWindows = @()
$checks = @()
$status = 'blocked'
$failure = $null
try {
    if ($parentProcess.SessionId -ne $parentSession) { throw 'Parent process session does not match the run.' }
    Assert-ParentActive
    # Move to a new name atomically. Only the first correct-session launcher may continue.
    Write-RunFile 'child-ready.json' @{ readyUtc = [DateTime]::UtcNow.ToString('o') }
    $ownsRun = $true
    $tester = Join-Path $BundleRoot 'tester/WindowsComputerUse.Tests.exe'
    $server = Join-Path $BundleRoot 'server/WindowsComputerUse.Server.exe'
    $worker = Join-Path $BundleRoot 'worker/WindowsComputerUse.Worker.exe'
    foreach ($exe in @($tester,$server,$worker)) {
        if (-not [IO.File]::Exists($exe)) { throw "Missing test bundle file: $exe" }
    }
    Write-Host 'Child session ready. Return to the main desktop test window.'
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while (-not [IO.File]::Exists((Join-Path $RunDirectory 'parent-start.json'))) {
        Assert-ParentActive
        if ($clock.Elapsed.TotalSeconds -gt 180) { throw 'Timed out waiting for the main desktop to start the test.' }
        Start-Sleep -Milliseconds 150
    }
    $gate = Read-RunFile 'parent-start.json'
    if ($gate.parentSessionId -ne $parentSession -or $gate.childSessionId -ne $childSession) { throw 'Start signal session mismatch.' }
    Assert-ParentActive

    $stdout = Join-Path $RunDirectory 'parallel-apps.stdout.log'
    $stderr = Join-Path $RunDirectory 'parallel-apps.stderr.log'
    # Windows paths cannot contain quotes. Trim the terminating separator before closing the argument quote.
    $arguments = '--parallel-apps --server "{0}" --report-dir "{1}" --run-id {2}' -f $server, $RunDirectory.TrimEnd('\'), $RunId
    $launch = [Diagnostics.ProcessStartInfo]::new($tester, $arguments)
    $launch.WorkingDirectory = $BundleRoot
    $launch.UseShellExecute = $false
    $launch.CreateNoWindow = $true
    $launch.RedirectStandardOutput = $true
    $launch.RedirectStandardError = $true
    $launch.EnvironmentVariables['WINDOWS_CU_WORKER_PATH'] = $worker
    $outFile = [IO.File]::Open($stdout, 'CreateNew', 'Write', 'Read')
    $errFile = [IO.File]::Open($stderr, 'CreateNew', 'Write', 'Read')
    $probe = [Diagnostics.Process]::Start($launch)
    $logTasks = @($probe.StandardOutput.BaseStream.CopyToAsync($outFile), $probe.StandardError.BaseStream.CopyToAsync($errFile))
    $started = $probe.StartTime.ToUniversalTime().ToString('o')
    Write-RunFile 'child-started.json' @{ startedUtc = $started; testerProcessId = $probe.Id }
    $clock.Restart()
    while (-not $probe.WaitForExit(150)) {
        Assert-ParentActive
        if ($clock.Elapsed.TotalSeconds -gt 180) { throw 'The child application checks exceeded 180 seconds.' }
    }
    $finished = $probe.ExitTime.ToUniversalTime().ToString('o')
    $exitCode = $probe.ExitCode
    $result = Read-RunFile 'parallel-apps.json'
    if ($result.checks -isnot [array] -or $result.inputWindows -isnot [array]) {
        throw 'Application tester report is missing its checks or input windows array.'
    }
    $checks = $result.checks
    $inputWindows = $result.inputWindows
    $passedNames = @($checks | Where-Object { $_.status -ceq 'passed' } | ForEach-Object { $_.name })
    $actions = @($inputWindows | ForEach-Object { $_.action })
    $missingApps = @('Notepad','Paint','Edge' | Where-Object { $passedNames -cnotcontains $_ })
    $missingActions = @('type_text','drag','click' | Where-Object { $actions -cnotcontains $_ })
    if ($exitCode -eq 0 -and $result.status -eq 'passed' -and $result.passed -eq $true -and
        $checks.Count -eq 3 -and $missingApps.Count -eq 0 -and $missingActions.Count -eq 0) {
        $status = 'passed'
    } else {
        $status = if ($exitCode -eq 2 -and $result.status -eq 'blocked') { 'blocked' } else { 'failed' }
        $reasons = @("Application tester exited with code $exitCode (reported $($result.status)).")
        if ($result.failure) { $reasons += $result.failure }
        if ($missingApps.Count) { $reasons += "Missing passed app checks: $($missingApps -join ', ')." }
        if ($missingActions.Count) { $reasons += "Missing successful input calls: $($missingActions -join ', ')." }
        $failure = ($reasons -join ' ') + ' See parallel-apps.json and its logs.'
    }
} catch {
    $failure = $_.Exception.Message
    if ($null -ne $probe) { $status = 'failed' }
    Write-Warning $failure
} finally {
    if ($null -ne $probe) {
        try {
            if (-not $probe.HasExited) {
                # Terminate the new tester and only its descendant processes on cancellation/timeout.
                $taskkill = Join-Path ([Environment]::SystemDirectory) 'taskkill.exe'
                & $taskkill /PID $probe.Id /T /F | Out-Host
                if (-not $probe.WaitForExit(5000)) { throw 'The child tester did not exit within five seconds.' }
            }
            if (-not $finished) { $finished = $probe.ExitTime.ToUniversalTime().ToString('o') }
        } catch {
            $failure = "$failure Cleanup: $($_.Exception.Message)"
            $status = 'failed'
        } finally {
            try {
                if ($logTasks.Count -and -not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]$logTasks, 3000)) {
                    Write-Warning 'Child log streams did not close before the timeout.'
                }
            } catch {
                Write-Warning "Could not finish copying child logs: $($_.Exception.Message)"
            } finally { $probe.Dispose() }
        }
    }
    if ($outFile) { $outFile.Dispose() }
    if ($errFile) { $errFile.Dispose() }
    if ($ownsRun) {
        $suites = @()
        if ($started) {
            $suites += @{ name = 'parallel-apps'; status = $status; startedUtc = $started; finishedUtc = $finished; report = 'parallel-apps.json' }
        }
        Write-RunFile 'child-result.json' @{
            status = $status; error = $failure; startedUtc = $started; finishedUtc = $finished
            checks = $checks; inputWindows = $inputWindows; suites = $suites
        }
    }
    $parentProcess.Dispose()
}
if ($status -eq 'passed') { exit 0 }
if ($status -eq 'blocked') { exit 2 }
exit 1
