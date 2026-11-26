$ErrorActionPreference = 'Stop'
# Exercise the classifier without starting the supervisor or loading user32.
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'Invoke-RdpChildConnectionCheck.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors -join [Environment]::NewLine) }
$fn = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-CredentialDialog'
}, $true)
if (!$fn) { throw 'Missing Get-CredentialDialog in the supervisor.' }
. ([scriptblock]::Create($fn.Extent.Text))

# Windows Server 2025 CI observed this checkpoint, broker, preview and WTS 1168.
$start = [DateTime]::UtcNow
$checkpoint = @{status='in_progress'; stage='connect-child'; hostOperation='poll-for-child-session'
    lastHostObservation='RDP state=2. RDP has not reported a connection.'; startedUtc=$start.ToString('o')
    childSessionId=$null; initiallyEnabled=$false; enableAttempted=$true; rdpWtsBindingVerified=$false}
$preview = @{pid=808; captionCategory='probe-preview'}
$broker = @{pid=7812; process='CredentialUIBroker'; foreground=$true; windowClass='Credential Dialog Xaml Host'
    captionCategory='security-or-credentials'; windowId=31; owner='probe'}

function Assert-Observation([string]$Case, [bool]$Expected, [scriptblock]$Change) {
    $c = $checkpoint.Clone()
    $p = $preview.Clone()
    $b = $broker.Clone()
    $w = @{lookupError=1168}
    . $Change
    $actual = Get-CredentialDialog $c @($p,$b) $w 808 $start
    if (($null -ne $actual) -ne $Expected) { throw "Incorrect credential observation: $Case." }
    if ($actual -and $actual.windowId -ne 31) { throw "Matched an unexpected window: $Case." }
    $script:passed++
}

$passed = 0
Assert-Observation 'matching probe owner' $true {}
Assert-Observation 'unowned broker is evidence for deadline classification' $true { $b.owner='unowned' }
Assert-Observation 'documented WTS no-child sentinel' $true { $w=@{childId=[uint32]::MaxValue} }
Assert-Observation 'another application owns the prompt' $false { $b.owner='external' }
Assert-Observation 'wrong foreground, caption, class or process' $false { $b.foreground=$false }
Assert-Observation 'different broker caption' $false { $b.captionCategory='unrecognized' }
Assert-Observation 'different top-level class' $false { $b.windowClass='#32770' }
Assert-Observation 'different host process' $false { $b.process='LogonUI' }
Assert-Observation 'unidentified window' $false { $b.windowId=0 }
Assert-Observation 'another preview' $false { $p.pid=55 }
Assert-Observation 'child already exists' $false { $w=@{childId=3} }
Assert-Observation 'ambiguous WTS result' $false { $w.childId=3 }
Assert-Observation 'WTS access error' $false { $w.lookupError=5 }
Assert-Observation 'RDP already connected' $false { $c.lastHostObservation='RDP state=1. Connected.' }
Assert-Observation 'wrong operation' $false { $c.hostOperation='rdp-connect' }
Assert-Observation 'another run' $false { $c.startedUtc=$start.AddMinutes(-1).ToString('o') }
Assert-Observation 'permanent setting was already enabled' $false { $c.initiallyEnabled=$true; $c.enableAttempted=$false }
Assert-Observation 'verification finished' $false { $c.rdpWtsBindingVerified=$true; $c.status='passed' }
Write-Output "$passed RDP credential observation checks passed."
