param(
    [string]$BundleRoot,
    [ValidateSet('Enable','Disable')][string]$ChildSessionSupport
)
$ErrorActionPreference = 'Stop'
if (-not $BundleRoot) {
    $BundleRoot = Split-Path -Parent $PSScriptRoot
    if ([IO.File]::Exists((Join-Path $BundleRoot 'WindowsComputerUse.slnx'))) { $BundleRoot = Join-Path $BundleRoot 'artifacts/win-x64' }
}
$BundleRoot = [IO.Path]::GetFullPath($BundleRoot)
. (Join-Path $PSScriptRoot 'ChildSessionHost.ps1')
if ($ChildSessionSupport) {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Requires Windows.' }
    Set-ChildSessionSupport -Enabled ($ChildSessionSupport -eq 'Enable')
    Write-Host ("Windows child sessions enabled: " + (Get-ChildSessionSupport))
    exit 0
}

function Write-RunJson([string]$name, $data) {
    $path = Join-Path $run $name
    $temp = "$path.$PID.tmp"
    [IO.File]::WriteAllText($temp, ($data | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temp, $path)
}
function Read-BoundResult([string]$name) {
    $path = Join-Path $run $name
    if (-not [IO.File]::Exists($path)) { return }
    $data = [IO.File]::ReadAllText($path) | ConvertFrom-Json
    if ($data.runId -cne $id -or $data.parentSessionId -ne $binding.parentSessionId -or $data.childSessionId -ne $binding.childSessionId) {
        throw "Mismatched run or Windows session in $name."
    }
    return $data
}

$id = [Guid]::NewGuid().ToString('N')
$run = Join-Path $BundleRoot "test-results/parallel/$id"
[void][IO.Directory]::CreateDirectory($run)
$reportPath = Join-Path $run 'REPORT.md'
Write-Host "Parallel desktop feasibility test. Report: $reportPath"
$session = $null
$binding = $null
$app = $null
$focus = $null
$stage = 'preflight'
$status = 'blocked'
$errorText = ''
try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Requires an interactive Windows desktop.' }
    if (-not [Environment]::UserInteractive -or [Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0) { throw 'Start this experiment in your signed-in Windows desktop.' }
    foreach ($file in @('tester/WindowsComputerUse.Tests.exe','server/WindowsComputerUse.Server.exe','worker/WindowsComputerUse.Worker.exe','scripts/Test-ParallelWorker.ps1')) {
        if (-not [IO.File]::Exists((Join-Path $BundleRoot $file))) { throw "Missing $file. Extract the complete new test ZIP." }
    }
    if (-not (Get-ChildSessionSupport)) { throw 'Windows child sessions are disabled. An administrator can explicitly run Start-Parallel-Test.cmd -ChildSessionSupport Enable, then rerun the test normally.' }
    $stage = 'connect-child'
    $launcher = Join-Path $run 'Run-Child.cmd'
    # All paths resolve from the launcher itself, including Chinese paths, spaces and percent signs.
    $cmd = "@echo off`r`npowershell.exe -NoProfile -ExecutionPolicy Bypass -File `"%~dp0..\..\..\scripts\Test-ParallelWorker.ps1`" -BundleRoot `"%~dp0..\..\..`" -RunDirectory `"%~dp0.`" -RunId $id`r`nif errorlevel 1 pause`r`n"
    Write-Host "Inside the child desktop, open this file: $launcher"
    $session = Open-ChildSessionHost -TimeoutSeconds 90 -Instruction "INSIDE this child desktop: open $launcher"
    if ($session.ParentSessionId -ne [Diagnostics.Process]::GetCurrentProcess().SessionId -or $session.ChildSessionId -le 0 -or $session.ChildSessionId -eq $session.ParentSessionId) {
        throw 'Could not establish distinct and valid Windows session identifiers.'
    }
    $binding = [ordered]@{runId=$id; parentProcessId=$PID; parentSessionId=$session.ParentSessionId; childSessionId=$session.ChildSessionId; machineName=[Environment]::MachineName}
    Write-RunJson 'parent-session.json' $binding
    [IO.File]::WriteAllText($launcher, $cmd, [Text.Encoding]::ASCII)
    $stage = 'wait-for-child'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Read-BoundResult 'child-ready.json')) {
        [Windows.Forms.Application]::DoEvents()
        $early = Read-BoundResult 'child-result.json'
        if ($early) { throw "Child startup: $($early.error)" }
        if (-not (Test-ChildSessionHostConnected $session)) { throw 'Child session preview disconnected before the worker was ready.' }
        if ($watch.Elapsed.TotalMinutes -ge 5) { throw "No child worker became ready within five minutes. Run the per-run Run-Child.cmd inside the child session." }
        Start-Sleep -Milliseconds 100
    }
    $stage = 'parallel-input'
    $focus = & (Join-Path $PSScriptRoot 'Test-ParallelFocus.ps1') -RunDirectory $run -RunId $id
    $app = Read-BoundResult 'child-result.json'
    if ($focus.status -eq 'failed' -or ($app -and $app.status -eq 'failed')) { $status = 'failed' }
    elseif ($focus.status -eq 'passed' -and $app -and $app.status -eq 'passed') { $status = 'passed' }
    elseif ($app -and $app.status -eq 'blocked') { $status = 'blocked' }
    else { $status = 'inconclusive' }
    $errorText = (@($app.error, $focus.reason) | Where-Object { $_ }) -join ' / '
} catch { $errorText = $_.Exception.Message }
finally {
    if ($binding -and -not $app -and -not [IO.File]::Exists((Join-Path $run 'parent-cancel.json'))) { Write-RunJson 'parent-cancel.json' $binding }
    if ($session) { try { Close-ChildSessionHost $session } catch { Write-Warning $_.Exception.Message } }
}
$summary = [ordered]@{runId=$id; status=$status; stage=$stage; detail=$errorText; os=[Environment]::OSVersion.VersionString; powershell=$PSVersionTable.PSVersion.ToString(); sessions=$binding; child=$app; focus=$focus}
Write-RunJson 'summary.json' $summary
$labels = @{ passed='通过'; failed='失败'; blocked='阻塞'; inconclusive='证据不足' }
$lines = @('# Windows 双桌面并行测试', '', "**结论　$($labels[$status])（$status）**", '', "阶段　$stage", '', $errorText, '', "本轮编号　$id")
if ($binding) { $lines += "主会话 $($binding.parentSessionId)，子会话 $($binding.childSessionId)。" }
if ($app) {
    $lines += @('', '| 子桌面应用 | 结果 |', '|---|---|')
    foreach ($check in $app.checks) { $lines += "| $($check.name) | $($check.status) |" }
    $lines += @('', '[子会话记录](child-result.json)')
    if ([IO.File]::Exists((Join-Path $run 'parallel-apps.json'))) { $lines += '[应用检查详情](parallel-apps.json)' }
}
if ($focus) { $lines += @('', "主桌面输入与焦点　$($focus.status)", '', '[键入、焦点与鼠标采样](focus.json)') }
$lines += @('', '结论范围为本轮三个应用操作及主桌面采样。[完整结果](summary.json)')
[IO.File]::WriteAllLines($reportPath, [string[]]$lines, [Text.UTF8Encoding]::new($false))
Write-Host "Result: $status. $errorText"
Write-Host "Report: $reportPath"
if ($status -eq 'passed') { exit 0 }
if ($status -eq 'failed') { exit 1 }
exit 2
