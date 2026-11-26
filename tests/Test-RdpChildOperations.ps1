param([string]$BundleRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'

# Use the exact C# literal supplied to Add-Type, including from an extracted ZIP.
# A fresh process keeps the fake host isolated from the real ActiveX test.
if ('ParallelDesktop.ChildRdpControl' -as [type]) { throw 'Start this test in a fresh PowerShell process.' }
$scriptPath = Join-Path $BundleRoot 'scripts/ChildSessionHost.ps1'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors -join [Environment]::NewLine) }
$sources = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.StringConstantExpressionAst] -and
        $node.Value.Contains('class ChildRdpControl')
}, $true))
if ($sources.Count -ne 1) { throw "Expected one embedded RDP control definition in $scriptPath." }
$fixture = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'fixtures/RdpControlStub.cs'))
Add-Type -TypeDefinition ($sources[0].Value + [Environment]::NewLine + $fixture)

function Assert-Operation {
    param([scriptblock]$Run, [string]$ExpectedCalls, [string]$Stage, [int]$Code)
    $errorText = $null
    try { & $Run | Out-Null } catch { $errorText = $_.Exception.Message }
    $observed = @([RdpTest.Trace]::Calls | Where-Object { $_ -ne 'GetOcx' }) -join ' '
    if ($observed -cne $ExpectedCalls) { throw "Expected '$ExpectedCalls', observed '$observed'. $errorText" }
    if ($Stage) {
        $diagnostic = 'RDP operation={0}, HRESULT=0x{1:X8}' -f $Stage, $Code
        if (-not $errorText -or -not $errorText.Contains($diagnostic)) { throw "Expected '$diagnostic', got '$errorText'." }
    } elseif ($null -ne $errorText) { throw $errorText }
    $script:passed++
}

$script:passed = 0
$control = [ParallelDesktop.ChildRdpControl]::new()
$connect = { $control.ConnectLocalChild(1024, 660) }
$stages = [ordered]@{
    Server = 'Server=localhost'
    DesktopWidth = 'DesktopWidth=1024'
    DesktopHeight = 'DesktopHeight=660'
    Put = 'ConnectToChildSession=true'
    AdvancedSettings7 = 'AdvancedSettings7'
    EnableCredSspSupport = 'EnableCredSspSupport=true'
    Connect = 'Connect'
}
$operations = @($stages.Keys)
$invalid = [int]0x80070057
$denied = [int]0x80070005
[RdpTest.Trace]::Reset()
Assert-Operation { $control.ConfigureLocalChild(1024, 660) } ($operations[0..5] -join ' ')
[RdpTest.Trace]::Reset()
Assert-Operation $connect ($operations -join ' ')

# A failure must report the original HRESULT and stop at the failed operation.
foreach ($i in 0..($operations.Count - 1)) {
    [RdpTest.Trace]::Reset()
    $operation = $operations[$i]
    [RdpTest.Trace]::FailureAt = $operation
    [RdpTest.Trace]::FailureCode = $invalid
    Assert-Operation $connect ($operations[0..$i] -join ' ') $stages[$operation] $invalid
}
foreach ($result in @($denied, 1)) { # E_ACCESSDENIED and S_FALSE from the child-mode setter.
    [RdpTest.Trace]::Reset()
    [RdpTest.Trace]::PutResult = $result
    Assert-Operation $connect ($operations[0..3] -join ' ') $stages.Put $result
}
[RdpTest.Trace]::Reset()
[RdpTest.Trace]::Control = [RdpTest.Control]::new() # Lacks IMsRdpExtendedSettings.
Assert-Operation $connect ($operations[0..2] -join ' ') 'QueryInterface(IMsRdpExtendedSettings)' ([int]0x80004002)
[RdpTest.Trace]::Reset()
[RdpTest.Trace]::FailureAt = 'GetOcx'
[RdpTest.Trace]::FailureCode = $invalid
Assert-Operation $connect '' 'GetOcx' $invalid
[RdpTest.Trace]::Reset()
[RdpTest.Trace]::Control = $null
Assert-Operation $connect '' 'GetOcx' ([int]0x80131509)

[RdpTest.Trace]::Reset()
Assert-Operation {
    if ($control.ConnectionState() -ne 1 -or $control.DisconnectReason() -ne 0) { throw 'Incorrect RDP state or reason.' }
    $control.DisconnectChild()
} 'Connected ExtendedDisconnectReason Disconnect'
$statusCalls = [ordered]@{
    Connected = { $control.ConnectionState() }
    ExtendedDisconnectReason = { $control.DisconnectReason() }
    Disconnect = { $control.DisconnectChild() }
}
foreach ($operation in $statusCalls.Keys) {
    [RdpTest.Trace]::Reset()
    [RdpTest.Trace]::FailureAt = $operation
    [RdpTest.Trace]::FailureCode = $denied
    Assert-Operation $statusCalls[$operation] $operation $operation $denied
}
Write-Output "$script:passed simulated RDP operation checks passed."
