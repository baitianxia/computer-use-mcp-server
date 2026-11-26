param(
    [Parameter(Mandatory)][string]$RunDirectory,
    [Parameter(Mandatory)][string]$RunId,
    [ValidateRange(5, 1800)][int]$TimeoutSeconds = 300
)
$ErrorActionPreference = 'Stop'

function Read-RunFile([string]$name) {
    $file = Join-Path $RunDirectory $name
    if (-not [IO.File]::Exists($file)) { return $null }
    $record = [IO.File]::ReadAllText($file) | ConvertFrom-Json
    if ($record.runId -cne $RunId -or $null -eq $record.parentSessionId -or $null -eq $record.childSessionId -or
        $record.parentSessionId -ne $parentId -or $record.childSessionId -ne $childId) {
        throw "Run or Windows session ID mismatch in $name."
    }
    return $record
}

function Write-NewRunFile([string]$name, $record) {
    $file = Join-Path $RunDirectory $name
    $temporary = "$file.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temporary, ($record | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporary, $file)
    } finally {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}

function Get-UnixMs($value) {
    if ($null -eq $value) { throw 'Missing worker observation time.' }
    if ($value -is [datetime]) { return ([datetimeoffset]$value).ToUnixTimeMilliseconds() }
    return [DateTimeOffset]::Parse($value, [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal).ToUnixTimeMilliseconds()
}

function Measure-ParallelFocus($result, $samples, $focusLosses, $trials, $child) {
    if (-not $child.startedUtc -or -not $child.finishedUtc) {
        $result.reason = 'Child probe did not report a measured interval.'
        return
    }
    $start = Get-UnixMs $child.startedUtc
    $end = Get-UnixMs $child.finishedUtc
    if ($end -le $start) { throw 'Invalid child observation interval.' }
    $result.childStartedUtc = $child.startedUtc
    $result.childFinishedUtc = $child.finishedUtc
    $lastMs = $start
    foreach ($sample in $samples) {
        if ($sample.at -lt $start -or $sample.at -gt $end) { continue }
        $result.sampleCount++
        if (-not $sample.focused) { $result.mismatchedSamples++ }
        if ($null -ne $sample.cursor) {
            $result.cursorSampleCount++
            if ($null -ne $result.cursorBaseline) {
                $dx = [double]$sample.cursor.X - $result.cursorBaseline.X
                $dy = [double]$sample.cursor.Y - $result.cursorBaseline.Y
                $distance = [Math]::Sqrt($dx * $dx + $dy * $dy)
                $result.maxCursorDistancePx = [Math]::Max([double]$result.maxCursorDistancePx, $distance)
            }
        }
        $result.maxSampleGapMs = [Math]::Max([long]$result.maxSampleGapMs, [long]($sample.at - $lastMs))
        $lastMs = $sample.at
    }
    $result.maxSampleGapMs = [Math]::Max([long]$result.maxSampleGapMs, [long]($end - $lastMs))
    foreach ($loss in $focusLosses) {
        if ($loss -ge $start -and $loss -le $end) { $result.focusLossEvents++ }
    }
    $windows = @(foreach ($window in $child.inputWindows) {
        $from = Get-UnixMs $window.startedUtc
        $to = Get-UnixMs $window.finishedUtc
        if ($from -lt $start -or $to -lt $from -or $to -gt $end) { throw 'Invalid child input-call interval.' }
        [pscustomobject]@{ from = $from; to = $to; action = $window.action }
    })
    $result.childInputWindows = $windows.Count
    foreach ($trial in $trials) {
        if ($trial.times.Count -eq 0 -or $trial.times[0] -lt $start -or $trial.times[-1] -gt $end) { continue }
        if (-not $trial.correct) { $result.incorrectPhrases++; continue }
        $result.correctPhrases++
        foreach ($at in $trial.times) {
            foreach ($window in $windows) {
                if ($at -ge $window.from -and $at -le $window.to) {
                    $result.overlappingKeypresses++
                    if ($null -eq $result.firstOverlap) {
                        $result.firstOverlap = [ordered]@{ action = $window.action; keypressUnixMs = $at
                            callStartedUnixMs = $window.from; callFinishedUnixMs = $window.to }
                    }
                    break
                }
            }
        }
    }
    if ($result.mismatchedSamples -gt 0 -or $result.focusLossEvents -gt 0) {
        $result.status = 'failed'
        $result.reason = 'The parent test form or its input field lost focus during the child probe.'
    } elseif ($child.status -ne 'passed') {
        $result.reason = 'Child probe did not pass.'
    } elseif ($samples.Count -lt 2 -or $samples[0].at -gt $start -or $samples[-1].at -lt $end -or
              $result.sampleCount -lt 2 -or $result.maxSampleGapMs -gt $result.sampleGapLimitMs) {
        $result.reason = 'Insufficient focus sample coverage during the child probe.'
    } elseif ($null -eq $result.cursorBaseline -or $result.cursorSampleCount -ne $result.sampleCount) {
        $result.reason = 'The parent cursor baseline or one or more physical cursor positions were unavailable.'
    } elseif ($result.maxCursorDistancePx -gt $result.cursorTolerancePx) {
        $result.reason = 'Parent cursor moved more than 2 physical pixels. Retry without moving the mouse.'
    } elseif ($result.incorrectPhrases -gt 0 -or $result.correctPhrases -eq 0) {
        $result.reason = 'Type the six digits repeatedly without pasting. The run needs complete, correct phrases.'
    } elseif ($result.overlappingKeypresses -eq 0) {
        $result.reason = 'No verified keystroke overlapped a successful child input call. Retry and keep typing.'
    } else {
        $result.status = 'passed'
        $result.reason = 'Typing overlapped a successful child input call. Focus and sampled cursor position stayed within limits.'
    }
}

$parentId = [Diagnostics.Process]::GetCurrentProcess().SessionId
$request = [IO.File]::ReadAllText((Join-Path $RunDirectory 'parent-session.json')) | ConvertFrom-Json
$childId = $request.childSessionId
if ($null -eq $childId -or $childId -eq $parentId -or $request.parentSessionId -ne $parentId -or $request.runId -cne $RunId) {
    throw 'Run this probe from the original parent Windows session.'
}
foreach ($name in @('parent-start.json', 'parent-cancel.json', 'focus.json')) {
    if ([IO.File]::Exists((Join-Path $RunDirectory $name))) { throw "Run is already used: $name." }
}
if (-not (Read-RunFile 'child-ready.json')) { throw 'Child session is not ready.' }
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
if (-not ('ParallelFocusNative' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ParallelFocusNative {
    [StructLayout(LayoutKind.Sequential)]
    public struct Point { public int X; public int Y; }
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetPhysicalCursorPos(out Point point);
}
'@
}
function Get-ParentSample {
    $point = [ParallelFocusNative+Point]::new()
    $position = if ([ParallelFocusNative]::GetPhysicalCursorPos([ref]$point)) { $point } else { $null }
    [pscustomobject]@{ at = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds(); cursor = $position
        focused = ([ParallelFocusNative]::GetForegroundWindow() -eq $form.Handle -and $box.Focused) }
}

$result = [ordered]@{
    runId = $RunId; parentSessionId = $parentId; childSessionId = $childId; status = 'inconclusive'
    reason = 'Parent observation was cancelled.'; childStartedUtc = $null; childFinishedUtc = $null
    sampleCount = 0; maxSampleGapMs = 0; sampleGapLimitMs = 500; mismatchedSamples = 0; focusLossEvents = 0
    cursorBaseline = $null; cursorSampleCount = 0; maxCursorDistancePx = $null; cursorTolerancePx = 2
    correctPhrases = 0; incorrectPhrases = 0; childInputWindows = 0; overlappingKeypresses = 0; firstOverlap = $null
}
$state = @{ started = $false; typing = $false; child = $null; error = $null }
$samples = [Collections.Generic.List[object]]::new()
$focusLosses = [Collections.Generic.List[long]]::new()
$trials = [Collections.Generic.List[object]]::new()
$keyTimes = [Collections.Generic.List[long]]::new()
$phrase = [string](Get-Random -Minimum 100000 -Maximum 1000000)
$form = [Windows.Forms.Form]::new()
$form.Text = 'Computer Use | 主桌面输入测试'
$form.StartPosition = 'CenterScreen'
$form.AutoScaleMode = 'Dpi'
$form.ClientSize = [Drawing.Size]::new(660, 200)
$label = [Windows.Forms.Label]::new()
$label.SetBounds(16, 16, 625, 53)
$label.Text = '子会话已就绪。切换为英文半角输入。点击“开始”后请将手离开鼠标或触控板，只用键盘输入。'
$box = [Windows.Forms.TextBox]::new()
$box.SetBounds(16, 80, 280, 36)
$box.Font = [Drawing.Font]::new('Segoe UI', 16)
$box.MaxLength = $phrase.Length
$box.ReadOnly = $true
$box.ShortcutsEnabled = $false
$box.ImeMode = 'Disable'
$startButton = [Windows.Forms.Button]::new()
$startButton.SetBounds(16, 142, 105, 36)
$startButton.Text = '开始'
$form.Controls.AddRange(@($label, $box, $startButton))
$clock = [Diagnostics.Stopwatch]::StartNew()
$timer = [Windows.Forms.Timer]::new()
$timer.Interval = 50

$startButton.Add_Click({
    try {
        if ($state.started) { return }
        $startButton.Enabled = $false
        if (-not $box.Focus() -or [ParallelFocusNative]::GetForegroundWindow() -ne $form.Handle) {
            throw 'Cannot start without focus in the parent input field.'
        }
        $state.started = $true
        $baseline = Get-ParentSample
        $result.cursorBaseline = $baseline.cursor
        $samples.Add($baseline)
        $label.Text = '已开始，等待子会话启动实际桌面操作。数字出现后请反复手动输入。'
        Write-NewRunFile 'parent-start.json' ([ordered]@{ runId = $RunId; parentSessionId = $parentId
            childSessionId = $childId; startedUtc = [DateTime]::UtcNow.ToString('o') })
        $clock.Restart()
    } catch { $state.error = $_.Exception.Message; $form.Close() }
})
$box.Add_KeyPress({
    param($sender, $e)
    if (-not $state.typing -or $e.KeyChar -lt [char]'0' -or $e.KeyChar -gt [char]'9') {
        $e.Handled = $true
        return
    }
    $keyTimes.Add([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())
})
$box.Add_TextChanged({
    if ($state.typing -and $box.Text.Length -eq $phrase.Length) {
        $trials.Add([pscustomobject]@{ correct = $box.Text -ceq $phrase -and $keyTimes.Count -eq $phrase.Length
            times = $keyTimes.ToArray() })
        $keyTimes.Clear()
        $box.Clear()
    }
})
$box.Add_Leave({ if ($state.started) { $focusLosses.Add([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()) } })
$form.Add_Deactivate({ if ($state.started) { $focusLosses.Add([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()) } })
$timer.Add_Tick({
    try {
        if ($state.started) {
            $samples.Add((Get-ParentSample))
            if (-not $state.typing -and (Read-RunFile 'child-started.json')) {
                $state.typing = $true
                $box.ReadOnly = $false
                $label.Text = "保持鼠标不动，反复手动输入 $phrase 。每输满 6 位会自动清空，请继续输入直到窗口关闭。"
            }
        }
        $state.child = Read-RunFile 'child-result.json'
        if ($null -ne $state.child) {
            $state.started = $false
            $form.Close()
        } elseif ($clock.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
            $state.error = 'Timed out waiting for the parent start or child result.'
            $form.Close()
        }
    } catch { $state.error = $_.Exception.Message; $form.Close() }
})

try {
    $timer.Start()
    [void]$form.ShowDialog()
    if ($state.error) { $result.reason = $state.error }
    elseif ($state.child) { Measure-ParallelFocus $result $samples $focusLosses $trials $state.child }
} catch {
    $result.status = 'inconclusive'
    $result.reason = $_.Exception.Message
} finally {
    $timer.Stop()
    $timer.Dispose()
    $form.Dispose()
    if (-not $state.child -and -not [IO.File]::Exists((Join-Path $RunDirectory 'parent-cancel.json'))) {
        Write-NewRunFile 'parent-cancel.json' ([ordered]@{ runId = $RunId; parentSessionId = $parentId
            childSessionId = $childId; cancelledUtc = [DateTime]::UtcNow.ToString('o') })
    }
    Write-NewRunFile 'focus.json' $result
}
[pscustomobject]$result
