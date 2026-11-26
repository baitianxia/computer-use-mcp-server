# Run exclusively on a disposable GitHub-hosted Windows VM. The parent under test
# signs in after child sessions are enabled. Only localhost and the test account are used.
param([Parameter(Mandatory)][string]$BundleRoot, [Parameter(Mandatory)][string]$ReportPath,
    [ValidateRange(15,120)][int]$TimeoutSeconds = 75)
$ErrorActionPreference='Stop'
$target=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ReportPath)
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
$innerEvidence=[IO.Path]::ChangeExtension($target,'inner.json')
$outerEvidence=[IO.Path]::ChangeExtension($target,'outer.json')
$r=[ordered]@{ status='blocked'; stage='preflight'; detail=''; startedUtc=[datetime]::UtcNow.ToString('o'); hostScriptSha256=$null;
    runnerSessionId=$null; freshParentSessionId=$null; childSessionId=$null; initiallyEnabled=$null; outer=$null; innerStatus=$null;
    outerDisconnectReason=$null; innerVerified=$false; cleanupErrors=@(); loginUi=@() }
$tag=[guid]::NewGuid().ToString('N').Substring(0,12)
$userName='CUrdp'+$tag
$taskName='CU-child-gate-'+$tag
$taskRegistered=$false; $account=$null; $sessionOwner=$null; $client=$null; $stageDir=$null
$secret=$null; $passwordBytes=$null; $grants=@(); $innerPath=$null; $outerPath=$null
function Write-State([string]$stage) {
    if ($stage) { $r.stage=$stage }
    $copy=[ordered]@{}+$r
    if ($stage) { $copy.status='in_progress' }
    $copy.updatedUtc=[datetime]::UtcNow.ToString('o')
    $tmp="$target.$PID.tmp"
    [IO.File]::WriteAllText($tmp,($copy|ConvertTo-Json -Depth 6))
    if ([IO.File]::Exists($target)) { [IO.File]::Replace($tmp,$target,[Management.Automation.Language.NullString]::Value) } else { [IO.File]::Move($tmp,$target) }
    if ($stage) { Write-Host "Fresh-parent check: $stage" }
}
function Read-FileIfPresent([string]$path) {
    if ($path -and [IO.File]::Exists($path)) { return ([IO.File]::ReadAllText($path) | ConvertFrom-Json) }
}
function Save-Observations {
    $r.loginUi=@(Get-Process -Name CredentialUIBroker,LogonUI -ErrorAction SilentlyContinue | ForEach-Object { @{name=$_.ProcessName; sessionId=$_.SessionId; pid=$_.Id} })
    if ($outerPath -and [IO.File]::Exists($outerPath)) {
        [IO.File]::Copy($outerPath,$outerEvidence,$true); $r.outer=Read-FileIfPresent $outerEvidence
    }
    if ($outerPath -and [IO.File]::Exists(($outerPath+'.disconnect'))) {
        $code=0; if ([int]::TryParse([IO.File]::ReadAllText(($outerPath+'.disconnect')),[ref]$code)) { $r.outerDisconnectReason=$code }
    }
    if ($innerPath -and [IO.File]::Exists($innerPath)) {
        [IO.File]::Copy($innerPath,$innerEvidence,$true); $r.innerStatus=(Read-FileIfPresent $innerEvidence).status
    }
}
function Access-Rule([string]$path,[string]$rule) {
    & icacls.exe $path /grant $rule | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not grant scoped test access to $path." }
}
function Stop-TestTask {
    if ($taskRegistered) {
        $task=Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
        if ($task.State -eq 'Running') { Stop-ScheduledTask -TaskName $taskName }
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    }
}
try {
    Write-State 'preflight'
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or $PSVersionTable.PSEdition -ne 'Desktop' -or
        [Threading.Thread]::CurrentThread.GetApartmentState() -ne [Threading.ApartmentState]::STA -or
        $env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or $env:RUNNER_OS -cne 'Windows') { throw 'Requires GitHub-hosted Windows PowerShell 5.1 STA.' }
    $r.runnerSessionId=[Diagnostics.Process]::GetCurrentProcess().SessionId
    if ($r.runnerSessionId -le 0 -or ![Environment]::UserInteractive) { throw 'Requires the interactive hosted session.' }
    if ((Get-ItemPropertyValue 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' 'fDenyTSConnections') -ne 0 -or
        -not @(Get-NetTCPConnection -LocalPort 3389 -State Listen -ErrorAction SilentlyContinue).Count) { throw 'Ordinary RDP is not already enabled and listening on port 3389.' }
    $BundleRoot=(Get-Item -LiteralPath $BundleRoot).FullName
    $hostFile=Join-Path $BundleRoot 'scripts/ChildSessionHost.ps1'
    $gateFile=Join-Path $PSScriptRoot 'Test-RdpChildConnection.ps1'
    $r.hostScriptSha256=(Get-FileHash -LiteralPath $hostFile).Hash
    . $hostFile
    Write-State 'enable-before-fresh-logon'
    $r.initiallyEnabled=Get-ChildSessionSupport
    $existing=Get-ChildSessionId -Stage before-connect
    if ($null -ne $existing -and $existing -ne [uint32]::MaxValue) { throw 'The original runner session already has a child. Refusing to disturb it.' }
    if (!$r.initiallyEnabled) { Set-ChildSessionSupport -Enabled $true }
    if (!(Get-ChildSessionSupport)) { throw 'Child-session enablement did not persist.' }

    Write-State 'prepare-test-account'
    if (Get-LocalUser -Name $userName -ErrorAction SilentlyContinue) { throw 'The randomly generated test account already exists.' }
    $random=New-Object byte[] 48
    $rng=[Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($random) } finally { $rng.Dispose() }
    $passwordBytes=New-Object byte[] 52
    [Array]::Copy([Text.Encoding]::ASCII.GetBytes('Aa1!'),$passwordBytes,4)
    for ($i=0;$i -lt $random.Length;$i++) { $passwordBytes[$i+4]=33+($random[$i] % 94) }
    [Array]::Clear($random,0,$random.Length)
    $secret=New-Object Security.SecureString
    foreach ($b in $passwordBytes) { $secret.AppendChar([char]$b) }; $secret.MakeReadOnly()
    $account=New-LocalUser -Name $userName -Password $secret -Description 'Disposable local child-session CI test' -AccountExpires ([datetime]::Now.AddHours(1))
    $sid=$account.SID.Value
    Add-LocalGroupMember -SID ([Security.Principal.SecurityIdentifier]'S-1-5-32-555') -Member $account
    $stageDir=Join-Path $env:ProgramData ('ComputerUseFreshParent-'+$tag)
    [void][IO.Directory]::CreateDirectory($stageDir)
    & icacls.exe $stageDir /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' ("*{0}:(OI)(CI)RX" -f $sid) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not isolate the per-run test directory.' }
    $out=Join-Path $stageDir out; [void][IO.Directory]::CreateDirectory($out)
    Access-Rule $out ("*{0}:(OI)(CI)M" -f $sid)
    foreach ($p in @($BundleRoot,(Join-Path $BundleRoot 'scripts'),$hostFile)) { Access-Rule $p ("*{0}:RX" -f $sid); $grants+= $p }
    $gate=Join-Path $stageDir 'gate.ps1'; [IO.File]::Copy($gateFile,$gate)
    $innerPath=Join-Path $out 'inner.json'; $outerPath=Join-Path $stageDir 'outer.json'
    $psExe=Join-Path $PSHOME powershell.exe
    $clientScript=Join-Path $PSScriptRoot 'fixtures/Open-LocalRdpParent.ps1'
    $psi=[Diagnostics.ProcessStartInfo]::new($psExe,('-NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -File "{0}" -TestUser {1} -BundleRoot "{2}" -StatePath "{3}"' -f $clientScript,$userName,$BundleRoot,$outerPath))
    $psi.UseShellExecute=$false; $psi.RedirectStandardInput=$true
    Write-State 'ordinary-localhost-logon'
    $client=[Diagnostics.Process]::Start($psi)
    $pipe=$client.StandardInput.BaseStream
    try { $pipe.Write($passwordBytes,0,$passwordBytes.Length); $pipe.Flush() } finally { $pipe.Close(); [Array]::Clear($passwordBytes,0,$passwordBytes.Length); $secret.Dispose(); $secret=$null }
    $clock=[Diagnostics.Stopwatch]::StartNew()
    do {
        $outer=Read-FileIfPresent $outerPath
        if (($outer -and $outer.stage -like 'failed-*') -or $client.HasExited) { throw 'The ordinary localhost RDP client stopped before a new authenticated desktop was confirmed.' }
        # A real user shell supplies an independent SID + session check; never use a logon token as desktop evidence.
        if ($outer -and $outer.state -eq 1) {
            foreach ($shell in @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'")) {
                if ($shell.SessionId -le 0 -or $shell.SessionId -eq $r.runnerSessionId) { continue }
                $owner=Invoke-CimMethod -InputObject $shell -MethodName GetOwnerSid -ErrorAction SilentlyContinue
                if ($owner -and $owner.ReturnValue -eq 0 -and $owner.Sid -eq $sid) { $sessionOwner=$shell; break }
            }
        }
        if (!$sessionOwner) { Start-Sleep -Milliseconds 500 }
    } while (!$sessionOwner -and $clock.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    if (!$sessionOwner) { throw 'No distinct, authenticated parent shell appeared before the deadline. Strict server authentication remained enabled.' }
    $r.freshParentSessionId=[int]$sessionOwner.SessionId
    Write-State 'start-inner-check-on-authenticated-desktop'
    $principal=New-ScheduledTaskPrincipal -UserId $sid -LogonType Interactive -RunLevel Limited
    $act=New-ScheduledTaskAction -Execute $psExe -Argument ('-NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -File "{0}" -BundleRoot "{1}" -ReportPath "{2}" -TimeoutSeconds {3}' -f $gate,$BundleRoot,$innerPath,$TimeoutSeconds)
    Register-ScheduledTask -TaskName $taskName -Action $act -Principal $principal | Out-Null
    $taskRegistered=$true
    $scheduler=New-Object -ComObject Schedule.Service; $scheduler.Connect()
    $registered=$scheduler.GetFolder('\').GetTask($taskName)
    [void]$registered.RunEx($null,4,$r.freshParentSessionId,([Environment]::MachineName+'\'+$userName))
    Write-State 'run-inner-production-connection'
    $clock.Restart(); $inner=$null
    do {
        $inner=Read-FileIfPresent $innerPath
        if ($inner -and $inner.status -ne 'in_progress') { break }
        if ($client.HasExited) { throw 'The fresh parent RDP client exited while the production probe was running.' }
        Start-Sleep -Milliseconds 400
    } while ($clock.Elapsed.TotalSeconds -lt ($TimeoutSeconds+10))
    if (!$inner -or $inner.status -eq 'in_progress') { throw 'The inner check did not finish. Its last available checkpoint is preserved.' }
    $r.innerStatus=$inner.status; $r.childSessionId=$inner.childSessionId
    if ($inner.status -eq 'blocked') { throw 'The authenticated parent reported the inner test blocked.' }
    $r.status='failed'
    if ($inner.status -ne 'passed' -or !$inner.rdpWtsBindingVerified -or !$inner.previewClosed -or !$inner.initiallyEnabled -or
        $inner.parentSessionId -ne $r.freshParentSessionId -or $inner.childSessionId -le 0 -or $inner.childSessionId -in @($r.runnerSessionId,$r.freshParentSessionId) -or
        $inner.hostScriptSha256 -ne $r.hostScriptSha256 -or $inner.testScriptSha256 -ne (Get-FileHash -LiteralPath $gateFile).Hash) { throw 'Inner check did not confirm the required IDs, script hashes, and closed preview.' }
    $r.status='passed'; $r.innerVerified=$true; $r.detail='Fresh parent interactive logon, distinct child RDP/WTS binding and preview closure verified.'
} catch { $r.detail=$_.Exception.Message }
finally {
    foreach ($action in @(
        { Save-Observations },
        { Stop-TestTask },
        { if ($sessionOwner -and (Get-Process -Id $sessionOwner.ProcessId -ErrorAction SilentlyContinue)) {
            $current=Get-CimInstance Win32_Process -Filter ("ProcessId={0}" -f $sessionOwner.ProcessId)
            if ($current -and $current.Name -ieq 'explorer.exe' -and $current.SessionId -eq $r.freshParentSessionId -and $current.SessionId -ne $r.runnerSessionId -and
                (Invoke-CimMethod -InputObject $current -MethodName GetOwnerSid).Sid -eq $account.SID.Value) {
                & logoff.exe $r.freshParentSessionId; if ($LASTEXITCODE -ne 0) { throw 'Fresh test user logoff failed.' }
            }
        } },
        { if ($client -and !$client.HasExited) { $client.Kill(); [void]$client.WaitForExit(5000) } },
        { foreach ($p in $grants) { & icacls.exe $p /remove:g ('*'+$account.SID.Value) | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'Test access cleanup failed.' } } },
        { if ($account) { Remove-LocalUser -SID $account.SID } },
        { if ($stageDir -and [IO.Directory]::Exists($stageDir)) {
            $outDir=Join-Path $stageDir out
            if ([IO.Directory]::Exists($outDir)) {
                foreach ($f in [IO.Directory]::GetFiles($outDir)) { [IO.File]::Delete($f) }
                [IO.Directory]::Delete($outDir,$false)
            }
            foreach ($f in [IO.Directory]::GetFiles($stageDir)) { [IO.File]::Delete($f) }
            [IO.Directory]::Delete($stageDir,$false)
        } },
        { if ($secret) { $secret.Dispose() }; if ($passwordBytes) { [Array]::Clear($passwordBytes,0,$passwordBytes.Length) } }
    )) { try { & $action } catch { $r.cleanupErrors+=@($_.Exception.Message) } }
    if ($r.cleanupErrors.Count -gt 0 -and $r.status -eq 'passed') { $r.status='failed'; $r.detail='The test passed but cleanup failed.' }
    Write-State
}
Write-Output "Fresh parent: $($r.status) at $($r.stage). $($r.detail)"
if ($r.status -eq 'passed') { exit 0 }; if ($r.status -eq 'blocked') { exit 2 }; exit 1
