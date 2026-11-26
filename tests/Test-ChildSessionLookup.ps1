$ErrorActionPreference = 'Stop'

# Run in a fresh process so every lookup uses this fake, including on Windows.
if ('ParallelDesktop.ChildSessions' -as [type]) { throw 'Start this test in a fresh PowerShell process.' }
Add-Type -TypeDefinition @'
using System.ComponentModel;
namespace ParallelDesktop {
    public static class ChildSessions {
        public static uint Result;
        public static int ErrorCode;
        public static uint ChildId() {
            if (ErrorCode != 0) throw new Win32Exception(ErrorCode, "Simulated native error.");
            return Result;
        }
    }
}
'@
. (Join-Path $PSScriptRoot '../scripts/ChildSessionHost.ps1')

function Assert-LookupError {
    param([string]$Stage, [int]$Code, [scriptblock]$Lookup)
    [ParallelDesktop.ChildSessions]::ErrorCode = $Code
    try {
        & $Lookup | Out-Null
    } catch {
        $message = $_.Exception.Message
        $decimal = '\b{0}\b' -f $Code
        $hex = '0x0*{0:x}\b' -f $Code
        if ($message.Contains($Stage) -and $message -match $decimal -and $message -match $hex) { return }
        throw "Expected $Stage and Win32 $Code in decimal and hex. Got: $message"
    }
    throw "Expected $Stage to throw for Win32 $Code."
}

$stages = @('before-connect', 'await-child-id', 'verify-bound-child', 'disable-check')
foreach ($stage in $stages) {
    [ParallelDesktop.ChildSessions]::ErrorCode = 0
    foreach ($expected in @([uint32]41, [uint32]::MaxValue)) {
        [ParallelDesktop.ChildSessions]::Result = $expected
        $actual = Get-ChildSessionId -Stage $stage
        if ($actual -isnot [uint32] -or $actual -ne $expected) { throw "$stage returned '$actual' for $expected." }
    }
    $lookup = { Get-ChildSessionId -Stage $stage }
    foreach ($code in @(2, 5, 87)) { Assert-LookupError -Stage $stage -Code $code -Lookup $lookup }
    if ($stage -in @('before-connect', 'await-child-id')) {
        [ParallelDesktop.ChildSessions]::ErrorCode = 1168
        if ($null -ne (& $lookup)) { throw "$stage must return null for Win32 1168." }
    } else {
        Assert-LookupError -Stage $stage -Code 1168 -Lookup $lookup
    }
}

try {
    Get-ChildSessionId -Stage 'unknown' | Out-Null
    throw 'An unknown lookup stage was accepted.'
} catch [System.Management.Automation.ParameterBindingException] {
    if ($_.FullyQualifiedErrorId -notlike 'ParameterArgumentValidationError,*') { throw }
}

$control = [pscustomobject]@{ State = 1 }
$control | Add-Member -MemberType ScriptMethod -Name ConnectionState -Value { return $this.State }
$child = [pscustomobject]@{ Form = [pscustomobject]@{ IsDisposed = $false }; Control = $control; ChildSessionId = [uint32]41 }
[ParallelDesktop.ChildSessions]::ErrorCode = 0
[ParallelDesktop.ChildSessions]::Result = $child.ChildSessionId
if (-not (Test-ChildSessionHostConnected -Child $child)) { throw 'The bound session should be connected.' }
foreach ($other in @([uint32]42, [uint32]::MaxValue)) {
    [ParallelDesktop.ChildSessions]::Result = $other
    if (Test-ChildSessionHostConnected -Child $child) { throw "Unexpectedly accepted session $other as the bound session." }
}
Assert-LookupError -Stage 'verify-bound-child' -Code 1168 -Lookup { Test-ChildSessionHostConnected -Child $child }
$control.State = 0
if (Test-ChildSessionHostConnected -Child $child) { throw 'A disconnected control should invalidate the binding.' }

Write-Output 'Child session lookup checks passed.'
