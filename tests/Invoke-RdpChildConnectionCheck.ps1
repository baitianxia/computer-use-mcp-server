#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$BundleRoot,
    [Parameter(Mandatory)][string]$ReportPath,
    [switch]$EnableOnHostedRunner,
    [ValidateRange(5,300)][int]$TimeoutSeconds = 90,
    [ValidateRange(5,360)][int]$ProcessTimeoutSeconds = 125
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The supervised RDP check requires Windows and PowerShell 7.' }

# Only read top-level captions in the probe and known credential UI processes.
# Publish a fixed category, never the caption, control contents or an image.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
public static class RdpProbeWindows {
    private delegate bool Visitor(IntPtr window, IntPtr unused);
    [DllImport("user32")] private static extern bool EnumWindows(Visitor visit, IntPtr unused);
    [DllImport("user32")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("user32", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr w, StringBuilder text, int max);
    [DllImport("user32", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr w, StringBuilder text, int max);
    [DllImport("wtsapi32", SetLastError=true)] private static extern bool WTSGetChildSessionId(out uint id);
    [DllImport("wtsapi32", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool WTSQuerySessionInformation(IntPtr server, uint id, int infoClass, out IntPtr data, out int bytes);
    [DllImport("wtsapi32")] private static extern void WTSFreeMemory(IntPtr data);

    public static Task<Dictionary<string,object>> ReadChildState() { return Task.Run(() => ChildSessionState()); }
    private static Dictionary<string,object> ChildSessionState() {
        uint id;
        var view = new Dictionary<string,object>();
        if (!WTSGetChildSessionId(out id)) { view["lookupError"] = Marshal.GetLastWin32Error(); return view; }
        view["childId"] = id;
        if (id == UInt32.MaxValue) return view;
        IntPtr data;
        int bytes;
        if (!WTSQuerySessionInformation(IntPtr.Zero, id, 8, out data, out bytes)) {
            view["stateError"] = Marshal.GetLastWin32Error();
        } else {
            try { if (bytes == 4 && data != IntPtr.Zero) view["connectState"] = Marshal.ReadInt32(data); }
            finally { if (data != IntPtr.Zero) WTSFreeMemory(data); }
        }
        return view;
    }

    public sealed class Row {
        public int pid { get; set; }
        public string process { get; set; }
        public string windowClass { get; set; }
        public string captionCategory { get; set; }
        public bool foreground { get; set; }
    }

    public static Task<Row[]> ReadWindows(int probePid, int sessionId) { return Task.Run(() => Snapshot(probePid, sessionId)); }
    private static Row[] Snapshot(int probePid, int sessionId) {
        var targets = new Dictionary<int,string> { { probePid, "powershell" } };
        foreach (string name in new[] { "CredentialUIBroker", "LogonUI" })
            foreach (Process proc in Process.GetProcessesByName(name))
                using (proc) {
                    try { if (proc.SessionId == sessionId) targets[proc.Id] = name; }
                    catch (InvalidOperationException) { } // Process exited during enumeration.
                }
        var rows = new List<Row>();
        IntPtr foreground = GetForegroundWindow();
        EnumWindows(delegate(IntPtr w, IntPtr unused) {
            int pid;
            GetWindowThreadProcessId(w, out pid);
            string name;
            if (!targets.TryGetValue(pid, out name) || !IsWindowVisible(w)) return true;
            var caption = new StringBuilder(256);
            GetWindowText(w, caption, caption.Capacity);
            string category;
            switch (caption.ToString()) {
                case "Computer Use - child Windows desktop": category = "probe-preview"; break;
                case "Windows Security": case "Enter your credentials": category = "security-or-credentials"; break;
                case "Remote Desktop Connection": category = "remote-desktop"; break;
                default: category = "unrecognized"; break;
            }
            var cls = new StringBuilder(256);
            GetClassName(w, cls, cls.Capacity);
            rows.Add(new Row { pid=pid, process=name, windowClass=cls.ToString(), captionCategory=category, foreground=(w==foreground) });
            return true;
        }, IntPtr.Zero);
        return rows.ToArray();
    }
}
'@

function Get-RdpEvents([DateTime]$Since) {
    $channels = @('Microsoft-Windows-TerminalServices-RDPClient/Operational',
        'Microsoft-Windows-TerminalServices-LocalSessionManager/Operational',
        'Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational')
    foreach ($channel in $channels) {
        try {
            $events = Get-WinEvent -FilterHashtable @{LogName=$channel; StartTime=$Since} -MaxEvents 60 -ErrorAction Stop
        } catch {
            if ($_.FullyQualifiedErrorId -notlike 'NoMatchingEventsFound*') {
                [pscustomobject]@{channel=$channel; queryError=$_.Exception.GetType().Name}
            }
            continue
        }
        foreach ($entry in $events) {
            $fields = [ordered]@{}
            $xml = [xml]$entry.ToXml()
            $nodes = $xml.SelectNodes("//*[local-name()='EventData' or local-name()='UserData']//*[not(*)]")
            foreach ($node in $nodes) {
                $key = $node.GetAttribute('Name')
                if (!$key) { $key = $node.LocalName }
                if ($key -notmatch '^[A-Za-z0-9_]*(error|status|state|result|reason|hresult|event)[A-Za-z0-9_]*$') { continue }
                if ($node.InnerText -match '^(0x[0-9a-f]{1,16}|-?\d{1,20}|(Tcp|Rdp|TsSsl)(State|Event)[A-Za-z0-9_]{1,80})$') {
                    $fields[$key] = $node.InnerText
                }
            }
            # Some providers put state transitions and hex errors only in the localized message.
            $stateTokens = @([regex]::Matches([string]$entry.Message, '\b(?:Tcp|Rdp|TsSsl)(?:State|Event)[A-Za-z0-9_]{1,80}\b') | ForEach-Object Value | Select-Object -Unique)
            $codes = @([regex]::Matches([string]$entry.Message, '(?i)\b(?:error(?: code)?|status|HRESULT)\s*[:=]?\s*(0x[0-9a-f]{1,16}|-?\d{1,12})\b') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
            [pscustomobject]@{at=$entry.TimeCreated.ToUniversalTime().ToString('o'); channel=$channel; id=$entry.Id; level=$entry.Level; fields=$fields; states=$stateTokens; codes=$codes}
        }
    }
}

$target = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ReportPath)
$output = [IO.Path]::ChangeExtension($target, 'supervisor.json')
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
$start = [DateTime]::UtcNow
$record = [ordered]@{status='in_progress'; startedUtc=$start.ToString('o'); processTimeoutSeconds=$ProcessTimeoutSeconds
    probePid=$null; childExitCode=$null; checkpointStage=$null; wtsAtTimeout=$null; windows=@(); events=@(); diagnosticErrors=@()}
function Save-SupervisorReport {
    $temp = "$output.$PID.tmp"
    [IO.File]::WriteAllText($temp, ($record | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temp, $output, $true)
}
Save-SupervisorReport
$ps = [Diagnostics.ProcessStartInfo]::new()
$ps.FileName = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$ps.UseShellExecute = $false
$modules = @($env:PSModulePath -split ';' | Where-Object { $_ -and $_ -notmatch '\\PowerShell\\' })
if ($modules.Count) { $ps.Environment['PSModulePath'] = $modules -join ';' } else { [void]$ps.Environment.Remove('PSModulePath') }
foreach ($a in @('-NoProfile','-NonInteractive','-STA','-ExecutionPolicy','Bypass','-File',
        (Join-Path $PSScriptRoot 'Test-RdpChildConnection.ps1'),'-BundleRoot',([IO.Path]::GetFullPath($BundleRoot)),
        '-ReportPath',$target,'-TimeoutSeconds',[string]$TimeoutSeconds)) { $ps.ArgumentList.Add($a) }
if ($EnableOnHostedRunner) { $ps.ArgumentList.Add('-EnableOnHostedRunner') }
$proc = $null
$exitCode = 1
try {
    $proc = [Diagnostics.Process]::Start($ps)
    $record.probePid = $proc.Id
    if (!$proc.WaitForExit($ProcessTimeoutSeconds * 1000)) {
        $exitCode = 124
        $record.status = 'timed_out'
        Save-SupervisorReport
        $windowTask = [RdpProbeWindows]::ReadWindows($proc.Id, [Diagnostics.Process]::GetCurrentProcess().SessionId)
        $wtsTask = [RdpProbeWindows]::ReadChildState()
        try {
            if (!$windowTask.Wait(1500)) { throw [TimeoutException]::new() }
            $record.windows = $windowTask.Result
        }
        catch { $record.diagnosticErrors += "Window snapshot: $($_.Exception.GetType().Name)" }
        try {
            if (!$wtsTask.Wait(1500)) { throw [TimeoutException]::new() }
            $record.wtsAtTimeout = $wtsTask.Result
        }
        catch { $record.diagnosticErrors += "WTS snapshot: $($_.Exception.GetType().Name)" }
    } else {
        $record.childExitCode = $proc.ExitCode
        $exitCode = $proc.ExitCode
        $record.status = switch ($exitCode) { 0 {'passed'} 2 {'blocked'} default {'failed'} }
    }
} catch {
    $record.status = 'failed'
    $record.diagnosticErrors += "Probe execution: $($_.Exception.Message)"
    $exitCode = 1
} finally {
    if ($proc) {
        try {
            if (!$proc.HasExited) { $proc.Kill(); if (!$proc.WaitForExit(5000)) { throw 'Probe still running after termination.' } }
        } catch { $record.diagnosticErrors += "Probe cleanup: $($_.Exception.Message)" }
        $proc.Dispose()
    }
}
try {
    $checkpoint = Get-Content -LiteralPath $target -Raw -Encoding utf8 | ConvertFrom-Json
    if (!$checkpoint.startedUtc -or ([DateTimeOffset]$checkpoint.startedUtc).UtcDateTime -lt $start) { throw 'The checkpoint does not belong to this run.' }
    $record.checkpointStage = $checkpoint.stage
    if ($exitCode -eq 0 -and ($checkpoint.status -ne 'passed' -or !$checkpoint.rdpWtsBindingVerified -or !$checkpoint.previewClosed)) {
        throw 'Missing verified connection or closed preview.'
    }
} catch {
    $record.diagnosticErrors += "Checkpoint: $($_.Exception.Message)"
    if ($exitCode -eq 0) { $exitCode = 1; $record.status = 'failed' }
}
Save-SupervisorReport
try { $record.events = @(Get-RdpEvents $start.ToLocalTime()) }
catch { $record.diagnosticErrors += "RDP events: $($_.Exception.GetType().Name)" }
$record.finishedUtc = [DateTime]::UtcNow.ToString('o')
Save-SupervisorReport
Write-Host "RDP supervisor $($record.status), last checkpoint $($record.checkpointStage), exit $exitCode. Report: $output"
exit $exitCode
