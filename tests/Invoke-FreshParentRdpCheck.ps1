# Run exclusively on a disposable GitHub-hosted Windows VM. The parent under test
# signs in after child sessions are enabled. The target is the verified local machine name and the account is disposable.
param([Parameter(Mandatory)][string]$BundleRoot, [Parameter(Mandatory)][string]$ReportPath,
    [ValidateRange(15,120)][int]$TimeoutSeconds = 75, [switch]$UseProcessCaFixture)
$ErrorActionPreference='Stop'
$target=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ReportPath)
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
$innerEvidence=[IO.Path]::ChangeExtension($target,'inner.json')
$outerEvidence=[IO.Path]::ChangeExtension($target,'outer.json')
$r=[ordered]@{ status='blocked'; stage='preflight'; detail=''; startedUtc=[datetime]::UtcNow.ToString('o'); hostScriptSha256=$null;
    runnerSessionId=$null; freshParentSessionId=$null; childSessionId=$null; initiallyEnabled=$null; outer=$null; innerStatus=$null;
    outerDisconnectReason=$null; listenerCertificate=$null; outerEvents=@(); outerDialog=$null; testAccountLogons=$null; testAccountSessions=$null; innerVerified=$false; cleanupErrors=@(); loginUi=@() }
$tag=[guid]::NewGuid().ToString('N').Substring(0,12)
$userName='CUrdp'+$tag
$taskName='CU-child-gate-'+$tag
$taskRegistered=$false; $account=$null; $sessionOwner=$null; $client=$null; $stageDir=$null
$wrapper=$null; $emptyCaDir=$null; $innerSupervisorPath=$null
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
function Get-LocalListenerCertificateReport {
    param([string]$ExportBoundPublicPem)
    # The CI image includes pwsh 7; the Windows PowerShell 5.1 caller lacks MatchesHostname
    # and DisableCertificateDownloads. This offline diagnostic does not configure the RDP client.
    $probe = {
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $r = [ordered]@{ status='listener-query-unavailable'; selfSigned=$null; matchesHostName=$null; matchesLocalhost=$null
            chainBuild=$null; chainStatus=@(); certificateSource=$null; revocationMode='offline'; certificateDownloads=$false
            validNow=$null; selfIssued=$null; basicConstraintsPresent=$null; certificateAuthority=$null; signingKeyUsage=$null
            publicLeafExported=$false; leafExportStatus='not-requested' }
        $outputPath=$env:CU_RDP_TEST_BOUND_LEAF_PEM
        if ($outputPath) { $r.leafExportStatus='bound-leaf-required' }
        $cert=$null; $chain=$null; $cim=$null
        function Export-MatchingRdpLeaf($leaf, [string]$path) {
            try {
                $parentDir=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($path))
                if (![IO.Path]::IsPathFullyQualified($path) -or $path.StartsWith('\\') -or
                    [IO.Path]::GetDirectoryName($parentDir) -ne [IO.Path]::GetFullPath($env:ProgramData) -or
                    [IO.Path]::GetFileName($parentDir) -notmatch '^ComputerUseFreshParent-[0-9a-f]{12}$' -or
                    [IO.Path]::GetFileName($path) -notmatch '^[a-z0-9_-]+\.pem$' -or
                    ([IO.File]::GetAttributes($parentDir) -band [IO.FileAttributes]::ReparsePoint)) { return 'export-path-invalid' }
                $tmp=[IO.Path]::Combine($parentDir,([Guid]::NewGuid().ToString('N')+'.tmp'))
                $stream=[IO.File]::Open($tmp,'CreateNew','Write','None')
                try {
                    try {
                        $pem=[Text.Encoding]::ASCII.GetBytes($leaf.ExportCertificatePem())
                        $stream.Write($pem,0,$pem.Length)
                    } finally { $stream.Dispose() }
                    $reload=[Security.Cryptography.X509Certificates.X509Certificate2]::new($tmp)
                    try {
                        $sha256=[Security.Cryptography.HashAlgorithmName]::SHA256
                        if ($reload.HasPrivateKey -or $reload.GetCertHashString($sha256) -cne $leaf.GetCertHashString($sha256)) { return 'public-DER-mismatch' }
                    } finally { $reload.Dispose() }
                    [IO.File]::Move($tmp,$path)
                    return 'exported'
                } finally { if ([IO.File]::Exists($tmp)) { [IO.File]::Delete($tmp) } }
            } catch { return 'export-failed' }
        }
        try {
            try {
                $options=New-CimSessionOption -PacketPrivacy
                $cim=New-CimSession -ComputerName localhost -SessionOption $options
                $bindings=@(Get-CimInstance -CimSession $cim -Namespace 'root/cimv2/TerminalServices' -ClassName Win32_TSGeneralSetting -Filter "TerminalName='RDP-tcp'" -Property SSLCertificateSHA1Hash,SSLCertificateSHA1HashType -OperationTimeoutSec 5)
                if ($bindings.Count -eq 1) {
                    $id=$bindings[0].SSLCertificateSHA1Hash
                    if ($bindings[0].SSLCertificateSHA1HashType -eq 1) { $r.selfSigned=$true }
                }
                $r.status='listener-binding-unavailable'
            } catch { }
            if ($id -match '^[0-9a-f]{40}$') {
                $r.status='bound-leaf-unavailable'
                foreach ($store in @('Remote Desktop','My')) {
                    $cert=Get-Item -LiteralPath ("Cert:\LocalMachine\{0}\{1}" -f $store,$id) -ErrorAction SilentlyContinue
                    if ($cert -and $cert.Thumbprint -eq $id) { $r.certificateSource='listener-binding'; break }
                    $cert=$null
                }
            } else {
                $copies=@(Get-ChildItem -LiteralPath 'Cert:\LocalMachine\Remote Desktop' -ErrorAction SilentlyContinue | Where-Object {
                    $_.MatchesHostname([Environment]::MachineName,$true,$true)
                })
                if ($copies.Count -eq 1) { $cert=$copies[0]; $r.certificateSource='candidate-unconfirmed'; $r.selfSigned=$null }
            }
            if ($cert) {
                    $r.status='name-check-unavailable'
                    $r.matchesHostName=$cert.MatchesHostname([Environment]::MachineName,$true,$true)
                    $r.matchesLocalhost=$cert.MatchesHostname('localhost',$true,$true)
                    $now=[DateTime]::UtcNow
                    $r.validNow=($cert.NotBefore.ToUniversalTime() -le $now -and $now -le $cert.NotAfter.ToUniversalTime())
                    $r.selfIssued=([Convert]::ToBase64String($cert.IssuerName.RawData) -ceq [Convert]::ToBase64String($cert.SubjectName.RawData))
                    $constraints=@($cert.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' })
                    $keyUsages=@($cert.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.15' })
                    $r.basicConstraintsPresent=($constraints.Count -gt 0)
                    if ($constraints.Count -eq 1) {
                        $r.certificateAuthority=[Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($constraints[0],$constraints[0].Critical).CertificateAuthority
                    }
                    if ($keyUsages.Count -eq 1) {
                        $usage=[Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new($keyUsages[0],$keyUsages[0].Critical).KeyUsages
                        $issuerFlags=[Security.Cryptography.X509Certificates.X509KeyUsageFlags]'KeyCertSign, CrlSign'
                        $r.signingKeyUsage=($usage -band $issuerFlags) -ne 0
                    }
                    if ($outputPath -and $r.certificateSource -eq 'listener-binding') {
                        $r.leafExportStatus='leaf-requirements-unmet'
                        if ($r.selfSigned -eq $true -and $r.selfIssued -and $r.validNow -and $r.matchesHostName -and
                            $constraints.Count -le 1 -and $keyUsages.Count -le 1 -and !$r.certificateAuthority -and !$r.signingKeyUsage) {
                            $r.leafExportStatus=Export-MatchingRdpLeaf $cert $outputPath
                            $r.publicLeafExported=($r.leafExportStatus -eq 'exported')
                        }
                    }
                    $r.status='chain-check-unavailable'
                    $chain=[Security.Cryptography.X509Certificates.X509Chain]::new()
                    $chain.ChainPolicy.DisableCertificateDownloads=$true
                    $chain.ChainPolicy.RevocationMode='Offline'
                    $r.chainBuild=$chain.Build($cert)
                    $r.chainStatus=@($chain.ChainStatus | ForEach-Object { $_.Status.ToString() })
                    if ($null -eq $r.selfSigned -and $r.chainBuild -and $chain.ChainElements.Count -gt 1 -and $cert.Subject -ine $cert.Issuer) { $r.selfSigned=$false }
                    $r.status=if ($r.certificateSource -eq 'listener-binding') { 'observed' } else { 'candidate-unconfirmed' }
            }
        } catch { } finally {
            if ($cim) { $cim.Dispose() }; if ($chain) { $chain.Dispose() }; if ($cert) { $cert.Dispose() }
        }
        [Console]::Out.WriteLine(($r | ConvertTo-Json -Compress))
    }
    $process=$null
    try {
        $pwsh = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'PowerShell\7\pwsh.exe'
        if (![IO.File]::Exists($pwsh)) { return @{ status='diagnostic-runtime-unavailable' } }
        # Only the constant script is encoded. No machine, account, certificate or credential values enter arguments.
        $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($probe.ToString()))
        $start=[Diagnostics.ProcessStartInfo]::new($pwsh,('-NoLogo -NoProfile -NonInteractive -EncodedCommand '+$encoded))
        $start.UseShellExecute=$false; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        [void]$start.EnvironmentVariables.Remove('CU_RDP_TEST_BOUND_LEAF_PEM')
        if ($ExportBoundPublicPem) { $start.EnvironmentVariables['CU_RDP_TEST_BOUND_LEAF_PEM']=$ExportBoundPublicPem }
        $process=[Diagnostics.Process]::Start($start)
        $stdout=$process.StandardOutput.ReadToEndAsync(); $null=$process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(15000)) { $process.Kill(); return @{status='diagnostic-timeout'} }
        if ($process.ExitCode -ne 0) { return @{status='diagnostic-process-failed'} }
        return ($stdout.GetAwaiter().GetResult() | ConvertFrom-Json -ErrorAction Stop)
    } catch { return @{status='diagnostic-unavailable'} }
    finally { if ($process) { $process.Dispose() } }
}

function Initialize-LocalRdpDiagnostics {
    if (!('LocalRdpDiagnostics' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
public static class LocalRdpDiagnostics {
    private delegate bool WindowVisitor(IntPtr window, IntPtr ignored);
    [DllImport("user32")] private static extern bool EnumWindows(WindowVisitor visitor, IntPtr param);
    [DllImport("user32")] private static extern bool EnumChildWindows(IntPtr parent, WindowVisitor visitor, IntPtr param);
    [DllImport("user32")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
    [DllImport("user32",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window,StringBuilder text,int limit);
    [DllImport("user32",CharSet=CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr window,uint msg,IntPtr limit,StringBuilder text,uint flags,uint time,out IntPtr result);
    [StructLayout(LayoutKind.Sequential)] private struct Session { public int id; public IntPtr station; public int state; }
    [DllImport("wtsapi32",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool WTSEnumerateSessions(IntPtr server,int zero,int version,out IntPtr items,out int count);
    [DllImport("wtsapi32",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool WTSQuerySessionInformation(IntPtr server,int id,int field,out IntPtr data,out int size);
    [DllImport("wtsapi32")] private static extern void WTSFreeMemory(IntPtr memory);
    public sealed class OwnedSession {
        public int id {get;set;} public int state {get;set;} public int protocol {get;set;}
    }
    private static string WtsText(int id,int field) {
        IntPtr p; int size;
        if (!WTSQuerySessionInformation(IntPtr.Zero,id,field,out p,out size)) return null;
        try { return p != IntPtr.Zero && size >= 2 ? Marshal.PtrToStringUni(p) : null; }
        finally { WTSFreeMemory(p); }
    }
    public static OwnedSession[] FindOwnSessions(string testUser,string testSid,int runnerId) {
        IntPtr p; int count;
        if (!WTSEnumerateSessions(IntPtr.Zero,0,1,out p,out count)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var found = new List<OwnedSession>();
        try {
            int size = Marshal.SizeOf(typeof(Session));
            for (int i=0;i<count;i++) {
                var s = (Session)Marshal.PtrToStructure(IntPtr.Add(p,i*size),typeof(Session));
                if (s.id<=0 || s.id==runnerId || !String.Equals(WtsText(s.id,5),testUser,StringComparison.OrdinalIgnoreCase)) continue;
                string domain = WtsText(s.id,7);
                if (!String.Equals(domain,Environment.MachineName,StringComparison.OrdinalIgnoreCase)) continue;
                var account = new System.Security.Principal.NTAccount(domain,testUser);
                var sid = (System.Security.Principal.SecurityIdentifier)account.Translate(typeof(System.Security.Principal.SecurityIdentifier));
                if (sid.Value != testSid) continue;
                var owned = new OwnedSession { id=s.id,state=s.state,protocol=-1 };
                IntPtr data; int bytes;
                if (WTSQuerySessionInformation(IntPtr.Zero,s.id,16,out data,out bytes)) {
                    try { if (data!=IntPtr.Zero && bytes>=2) owned.protocol = Marshal.ReadInt16(data); }
                    finally { WTSFreeMemory(data); }
                }
                found.Add(owned);
            }
        } finally { WTSFreeMemory(p); }
        return found.ToArray();
    }
    public sealed class Summary {
        public int visibleDialogs {get;set;}
        public bool limitReached {get;set;}
        public List<string> categories = new List<string>();
        public List<string> errorCodes = new List<string>();
        public void Classify(string text) {
            if (Regex.IsMatch(text, @"certificate|identity of the remote computer|identity of the computer", RegexOptions.IgnoreCase)) Add("certificate-or-identity");
            if (Regex.IsMatch(text, @"credentials|logon attempt failed|logon failure", RegexOptions.IgnoreCase)) Add("credentials-or-logon");
            if (Regex.IsMatch(text, @"console session|already connected|another session|number of connections", RegexOptions.IgnoreCase)) Add("session-or-limit");
            if (Regex.IsMatch(text, @"authentication error|security package|CredSSP", RegexOptions.IgnoreCase)) Add("authentication-error");
            if (Regex.IsMatch(text, @"can't connect|cannot connect|could not connect|couldn't connect", RegexOptions.IgnoreCase)) Add("connection-failed");
            foreach (Match m in Regex.Matches(text, @"\b(?:extended error code|error code|HRESULT|status)\s*[:=]\s*(0x[0-9a-f]{1,8}|[0-9]{1,10})\b", RegexOptions.IgnoreCase)) {
                string code = m.Groups[1].Value;
                if (!errorCodes.Contains(code) && errorCodes.Count < 8) errorCodes.Add(code);
            }
        }
        private void Add(string category) { if (!categories.Contains(category)) categories.Add(category); }
    }
    private static bool MatchWindow(IntPtr w, int pid, string kind) {
        int owner; GetWindowThreadProcessId(w, out owner);
        if (owner != pid || !IsWindowVisible(w)) return false;
        var name = new StringBuilder(64); GetClassName(w,name,name.Capacity);
        return String.Equals(name.ToString(),kind,StringComparison.OrdinalIgnoreCase);
    }
    public static Summary Read(int pid) {
        var data = new Summary();
        int controls = 0;
        var clock = Stopwatch.StartNew();
        // Only static labels of this RDP process's visible Win32 dialogs; no edit controls.
        EnumWindows(delegate(IntPtr w,IntPtr p) {
            if (data.visibleDialogs >= 8 || clock.ElapsedMilliseconds > 1200) { data.limitReached = true; return false; }
            if (!MatchWindow(w,pid,"#32770")) return true;
            data.visibleDialogs++;
            EnumChildWindows(w,delegate(IntPtr child, IntPtr ignored) {
                if (controls >= 32 || clock.ElapsedMilliseconds > 1200) { data.limitReached = true; return false; }
                if (!MatchWindow(child,pid,"Static")) return true;
                controls++;
                var text = new StringBuilder(768); IntPtr length;
                if (SendMessageTimeout(child,0x000D,new IntPtr(text.Capacity),text,3,60,out length) != IntPtr.Zero)
                    data.Classify(text.ToString());
                return true;
            },IntPtr.Zero);
            return true;
        },IntPtr.Zero);
        return data;
    }
}
'@
    }
}
function Get-ClientDialogSummary([int]$clientId) {
    Initialize-LocalRdpDiagnostics
    $view=[LocalRdpDiagnostics]::Read($clientId)
    return @{ visibleDialogs=$view.visibleDialogs; limitReached=$view.limitReached; categories=@($view.categories.ToArray()); errorCodes=@($view.errorCodes.ToArray()) }
}

function Get-TestAccountAudit {
    $log=@{ query='unavailable'; events=@(); successfulRemoteInteractive=$false; limitReached=$false }
    $since=$r.startedUtc
    if ($r.processCa) {
        # The fixture separately validates the new account with one local network logon.
        # Scope this audit to the actual RDP attempt; both can generate logon type 3.
        if (!$r.processCa.rdpAuditStartsUtc) { $log.query='positive-rdp-not-started'; return $log }
        $since=$r.processCa.rdpAuditStartsUtc
    }
    $time=([datetimeoffset]$since).UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ss.fffffffZ')
    $nameTests=@($userName,$userName.ToLowerInvariant(),$userName.ToUpperInvariant()) | ForEach-Object { "Data[@Name='TargetUserName']='$_'" }
    $who="Data[@Name='TargetUserSid']='$sid' or "+($nameTests -join ' or ')
    $query="*[System[(EventID=4624 or EventID=4625) and TimeCreated[@SystemTime >= '$time']] and EventData[$who]]"
    try {
        $entries=@(Get-WinEvent -LogName Security -FilterXPath $query -MaxEvents 33 -ErrorAction Stop)
        $log.query='read'; $log.limitReached=($entries.Count -gt 32)
        foreach ($entry in ($entries | Select-Object -First 32)) {
            $data=@{}
            foreach ($node in ([xml]$entry.ToXml()).Event.EventData.Data) { $data[$node.GetAttribute('Name')]=$node.InnerText }
            if ($data.TargetUserSid -ne $sid -and $data.TargetUserName -ine $userName) { continue }
            $item=@{ id=[int]$entry.Id; at=$entry.TimeCreated.ToUniversalTime().ToString('o') }
            foreach ($key in @('LogonType','Status','SubStatus')) {
                if ($data[$key] -match '^(?:\d{1,3}|0x[0-9a-fA-F]{1,8})$') { $item[$key]=$data[$key] }
            }
            if ($entry.Id -eq 4624 -and $item.LogonType -eq '10') { $log.successfulRemoteInteractive=$true }
            $log.events+=@($item)
        }
    } catch { if ($_.FullyQualifiedErrorId -like 'NoMatchingEventsFound*') { $log.query='no-matching-records' } }
    return $log
}

function Get-OuterEvents {
    $map=@{1='OnConnecting';2='OnConnected';3='OnLoginComplete';4='OnDisconnected';10='OnFatalError';
        18='OnAuthenticationWarningDisplayed';19='OnAuthenticationWarningDismissed';22='OnLogonError'}
    if (!$outerPath -or ![IO.File]::Exists(($outerPath+'.events'))) { return }
    try {
        $stream=[IO.File]::Open(($outerPath+'.events'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
        try { $reader=[IO.StreamReader]::new($stream); $lines=$reader.ReadToEnd() -split '\r?\n' }
        finally { $stream.Dispose() }
    } catch [IO.IOException] { return }
    foreach ($line in ($lines | Select-Object -Last 32)) {
        if ($line -notmatch '^(\d{1,2}) (-?\d{1,11}|-)$') { continue }
        $id=[int]$Matches[1]; $value=$Matches[2]
        if (!$map.ContainsKey($id)) { continue }
        $item=@{ dispid=$id; name=$map[$id] }
        if ($value -ne '-') { $code=0; if ([int]::TryParse($value,[ref]$code)) { $item.code=$code } }
        $item
    }
}


function Save-Observations {
    if ($innerPath -and [IO.File]::Exists($innerPath)) {
        [IO.File]::Copy($innerPath,$innerEvidence,$true); $r.innerStatus=(Read-FileIfPresent $innerEvidence).status
    }
    if ($innerSupervisorPath -and [IO.File]::Exists($innerSupervisorPath)) {
        [IO.File]::Copy($innerSupervisorPath,[IO.Path]::ChangeExtension($target,'inner.supervisor.json'),$true)
    }
    if ($wrapper) {
        if ($wrapper.Client.HasExited) { $wrapper.Drain(); $r.processCa.positiveExitCode=$wrapper.Client.ExitCode }
        $r.processCa.positiveTags=@($wrapper.Tags())
    }
    $r.loginUi=@(Get-Process -Name CredentialUIBroker,LogonUI -ErrorAction SilentlyContinue | ForEach-Object { @{name=$_.ProcessName; sessionId=$_.SessionId; pid=$_.Id} })
    if ($outerPath -and [IO.File]::Exists($outerPath)) {
        [IO.File]::Copy($outerPath,$outerEvidence,$true); $r.outer=Read-FileIfPresent $outerEvidence
    }
    if ($account -and $client) {
        $r.outerEvents=@(Get-OuterEvents)
        $disconnected=@($r.outerEvents | Where-Object name -eq OnDisconnected)
        if ($disconnected.Count) { $r.outerDisconnectReason=$disconnected[-1].code }
        $r.testAccountLogons=Get-TestAccountAudit
        $r.testAccountSessions=@([LocalRdpDiagnostics]::FindOwnSessions($userName,$sid,$r.runnerSessionId))
        if (!$client.HasExited) { $r.outerDialog=Get-ClientDialogSummary $client.Id }
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
    if ($UseProcessCaFixture) {
        . (Join-Path $PSScriptRoot 'fixtures/ProcessCaRdpParent.ps1')
        Initialize-ProcessCaClient
        $machine=[ProcessCaRdpClient]::VerifiedLocalMachineName()
        $r.processCa=@{ endpoint='127.0.0.1:3389'; verificationNameSource='verified-machine-name'; security='nla'; certificatePolicy='deny';
            negative=$null; stdinProbe=$null; localCredentialCheck=$null; rdpAuditStartsUtc=$null; loopbackConnectionObserved=$false; positiveTags=@(); positiveExitCode=$null }
        Write-State 'check-raw-stdin-transport'
        $r.processCa.stdinProbe=Test-ProcessCaStdin (Join-Path $PSHOME powershell.exe) $env:TEMP
        if ($r.processCa.stdinProbe.current -ne 'exact') { throw 'The raw stdin sentinel did not match its expected bytes and LF.' }
        # A known host, extra CA, or vendor config could silently add an alternate trust source.
        foreach ($base in @([Environment]::GetFolderPath('ApplicationData'),[Environment]::GetFolderPath('CommonApplicationData'),'C:\msys64\ucrt64\etc')) {
            $existingConfig=Join-Path $base 'freerdp'
            if ([IO.Directory]::Exists($existingConfig) -and @(Get-ChildItem -LiteralPath $existingConfig -Recurse -File -Force -ErrorAction Stop |
                Where-Object { $_.Name -in @('certificates.json','known_hosts','known_hosts2') -or $_.FullName -match '[\\/](certs|server)[\\/]' }).Count) {
                throw 'A pre-existing FreeRDP configuration is present. The fixture requires a clean client configuration.'
            }
        }
    } else { $r.listenerCertificate=Get-LocalListenerCertificateReport }
    Initialize-LocalRdpDiagnostics
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
    $gate=Join-Path $stageDir 'Test-RdpChildConnection.ps1'; [IO.File]::Copy($gateFile,$gate)
    $innerPath=Join-Path $out 'inner.json'; $outerPath=Join-Path $stageDir 'outer.json'
    $psExe=Join-Path $PSHOME powershell.exe
    if ($UseProcessCaFixture) {
        $supervisor=Join-Path $stageDir 'Invoke-RdpChildConnectionCheck.ps1'
        [IO.File]::Copy((Join-Path $PSScriptRoot 'Invoke-RdpChildConnectionCheck.ps1'),$supervisor)
        $innerSupervisorPath=[IO.Path]::ChangeExtension($innerPath,'supervisor.json')
        $pwshExe=Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'PowerShell\7\pwsh.exe'
        if (![IO.File]::Exists($pwshExe)) { throw 'The read-only inner supervisor requires the hosted PowerShell 7 runtime.' }
        Write-State 'export-bound-public-endpoint-certificate'
        $pem=Join-Path $stageDir 'listener.pem'
        $r.listenerCertificate=Get-LocalListenerCertificateReport -ExportBoundPublicPem $pem
        if ($r.listenerCertificate.publicLeafExported -ne $true -or $r.listenerCertificate.certificateSource -ne 'listener-binding') {
            throw 'Could not export the currently bound, time-valid local endpoint certificate.'
        }
        $emptyCaDir=Join-Path $stageDir 'ca-empty'; [void][IO.Directory]::CreateDirectory($emptyCaDir)
        $negativePem=Join-Path $stageDir 'negative.pem'; [IO.File]::WriteAllText($negativePem,'')
        Write-State 'reject-wrong-process-trust'
        $r.processCa.negative=Test-ProcessCaRejection $machine $tag $negativePem $emptyCaDir
        if (!$r.processCa.negative.confirmed) { throw 'Negative control did not explicitly reject the same endpoint certificate.' }
        Write-State 'check-new-local-account-once'
        $err=[ProcessCaRdpClient]::ValidateTemporaryAccount($userName,$secret)
        $r.processCa.localCredentialCheck=@{ succeeded=($err -eq 0); win32Error=$err; logonType=3 }
        if ($err -ne 0) { throw 'Windows rejected the local credential validation for the temporary account.' }
        Write-State 'ordinary-process-ca-logon'
        $r.processCa.rdpAuditStartsUtc=[datetime]::UtcNow.ToString('o')
        $wrapper=Start-ProcessCaClient $machine $userName $pem $emptyCaDir $passwordBytes
        $client=$wrapper.Client
    } else {
        $clientScript=Join-Path $PSScriptRoot 'fixtures/Open-LocalRdpParent.ps1'
        $psi=[Diagnostics.ProcessStartInfo]::new($psExe,('-NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -File "{0}" -TestUser {1} -BundleRoot "{2}" -StatePath "{3}"' -f $clientScript,$userName,$BundleRoot,$outerPath))
        $psi.UseShellExecute=$false; $psi.RedirectStandardInput=$true
        Write-State 'ordinary-machine-name-logon'
        $client=[Diagnostics.Process]::Start($psi)
        $pipe=$client.StandardInput.BaseStream
        try { $pipe.Write($passwordBytes,0,$passwordBytes.Length); $pipe.Flush() } finally { $pipe.Close() }
    }
    [Array]::Clear($passwordBytes,0,$passwordBytes.Length); $secret.Dispose(); $secret=$null
    $clock=[Diagnostics.Stopwatch]::StartNew()
    do {
        $outer=Read-FileIfPresent $outerPath
        if (($outer -and $outer.stage -like 'failed-*') -or $client.HasExited) { throw 'The ordinary local RDP client stopped before a new authenticated desktop was confirmed.' }
        if (@(Get-OuterEvents | Where-Object name -eq OnFatalError).Count) { throw 'The ordinary local RDP client reported OnFatalError.' }
        $connected=$outer -and $outer.state -eq 1
        if ($UseProcessCaFixture) {
            if (@($wrapper.Tags()) -contains 'unsafe-trust-decision') { throw 'The client reported an unexpected certificate acceptance path.' }
            $connected=@(Get-NetTCPConnection -OwningProcess $client.Id -State Established -RemotePort 3389 -ErrorAction SilentlyContinue |
                Where-Object { $_.RemoteAddress -eq '127.0.0.1' }).Count -gt 0
            if ($connected) { $r.processCa.loopbackConnectionObserved=$true }
        }
        if ($connected) {
            $active=@([LocalRdpDiagnostics]::FindOwnSessions($userName,$sid,$r.runnerSessionId) | Where-Object { $_.state -eq 0 -and $_.protocol -eq 2 })
            if ($active.Count -eq 1) { $sessionOwner=$active[0] }
        }
        if (!$sessionOwner) { Start-Sleep -Milliseconds 500 }
    } while (!$sessionOwner -and $clock.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    if (!$sessionOwner) { throw 'No connected local RDP client with a distinct, authenticated active WTS/RDP parent desktop was confirmed before the deadline.' }
    $r.freshParentSessionId=[int]$sessionOwner.id
    Write-State 'start-inner-check-on-authenticated-desktop'
    $principal=New-ScheduledTaskPrincipal -UserId $sid -LogonType Interactive -RunLevel Limited
    if ($UseProcessCaFixture) {
        $act=New-ScheduledTaskAction -Execute $pwshExe -Argument ('-NoProfile -NonInteractive -File "{0}" -BundleRoot "{1}" -ReportPath "{2}" -TimeoutSeconds 45 -ProcessTimeoutSeconds 70' -f $supervisor,$BundleRoot,$innerPath)
    } else {
        $act=New-ScheduledTaskAction -Execute $psExe -Argument ('-NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -File "{0}" -BundleRoot "{1}" -ReportPath "{2}" -TimeoutSeconds {3}' -f $gate,$BundleRoot,$innerPath,$TimeoutSeconds)
    }
    Register-ScheduledTask -TaskName $taskName -Action $act -Principal $principal | Out-Null
    $taskRegistered=$true
    $scheduler=New-Object -ComObject Schedule.Service; $scheduler.Connect()
    $registered=$scheduler.GetFolder('\').GetTask($taskName)
    [void]$registered.RunEx($null,4,$r.freshParentSessionId,([Environment]::MachineName+'\'+$userName))
    Write-State 'run-inner-production-connection'
    $clock.Restart(); $inner=$null; $supervised=$null
    $innerBudget=if ($UseProcessCaFixture) { 100 } else { $TimeoutSeconds+10 }
    do {
        $inner=Read-FileIfPresent $innerPath
        if ($UseProcessCaFixture) {
            $supervised=Read-FileIfPresent $innerSupervisorPath
            if ($supervised -and $supervised.finishedUtc) { break }
        } elseif ($inner -and $inner.status -ne 'in_progress') { break }
        if ($client.HasExited) { throw 'The fresh parent RDP client exited while the production probe was running.' }
        Start-Sleep -Milliseconds 400
    } while ($clock.Elapsed.TotalSeconds -lt $innerBudget)
    if ($UseProcessCaFixture -and (!$supervised -or !$supervised.finishedUtc -or $supervised.status -ne 'passed')) {
        if ($supervised -and $supervised.status -eq 'failed') { $r.status='failed' }
        throw 'The inner supervisor did not verify completion. The production checkpoint and supervisor report are preserved.'
    }
    if (!$inner -or $inner.status -eq 'in_progress') { throw 'The inner check did not finish. Its last available checkpoint is preserved.' }
    if ($UseProcessCaFixture -and (@($wrapper.Tags()) -contains 'unsafe-trust-decision' -or !$r.processCa.negative.confirmed -or !$r.processCa.loopbackConnectionObserved)) {
        throw 'The outer client did not preserve the required certificate and loopback evidence.'
    }
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
        { if ($account -and $client) {
            foreach ($testSession in [LocalRdpDiagnostics]::FindOwnSessions($userName,$sid,$r.runnerSessionId)) {
                & logoff.exe $testSession.id
                if ($LASTEXITCODE -ne 0) { throw 'Fresh test user logoff failed.' }
            }
        } },
        { if ($client -and !$client.HasExited) { $client.Kill(); [void]$client.WaitForExit(5000) } },
        { foreach ($p in $grants) { & icacls.exe $p /remove:g ('*'+$account.SID.Value) | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'Test access cleanup failed.' } } },
        { if ($account) { Remove-LocalUser -SID $account.SID } },
        { if ($stageDir -and [IO.Directory]::Exists($stageDir)) {
            if ($emptyCaDir -and [IO.Directory]::Exists($emptyCaDir)) { [IO.Directory]::Delete($emptyCaDir,$false) }
            $outDir=Join-Path $stageDir out
            if ([IO.Directory]::Exists($outDir)) {
                foreach ($f in [IO.Directory]::GetFiles($outDir)) { [IO.File]::Delete($f) }
                [IO.Directory]::Delete($outDir,$false)
            }
            foreach ($f in [IO.Directory]::GetFiles($stageDir)) { [IO.File]::Delete($f) }
            [IO.Directory]::Delete($stageDir,$false)
        } },
        { if ($wrapper) { $wrapper.Dispose() } },
        { if ($secret) { $secret.Dispose() }; if ($passwordBytes) { [Array]::Clear($passwordBytes,0,$passwordBytes.Length) } }
    )) { try { & $action } catch { $r.cleanupErrors+=@($_.Exception.Message) } }
    if ($r.cleanupErrors.Count -gt 0 -and $r.status -eq 'passed') { $r.status='failed'; $r.detail='The test passed but cleanup failed.' }
    Write-State
}
Write-Output "Fresh parent: $($r.status) at $($r.stage). $($r.detail)"
if ($r.status -eq 'passed') { exit 0 }; if ($r.status -eq 'blocked') { exit 2 }; exit 1
