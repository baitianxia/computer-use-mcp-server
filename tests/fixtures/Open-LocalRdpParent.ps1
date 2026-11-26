# Anonymous stdin carries the one-run user's password. No credentials are in arguments or files.
param([Parameter(Mandatory)][string]$BundleRoot, [Parameter(Mandatory)][ValidatePattern('^CUrdp[0-9a-f]{12}$')][string]$TestUser,
    [Parameter(Mandatory)][string]$StatePath)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or $env:RUNNER_OS -cne 'Windows') { throw 'Requires a disposable hosted Windows runner.' }
function Record-State([string]$stage, $rdp = $null, $reason = $null) {
    $data = @{ stage=$stage; state=$rdp; extendedDisconnectReason=$reason; eventHooks=$eventHooks; targetSource='operating-system-machine-name'; checkedAddressCount=$checkedAddressCount; utc=[DateTime]::UtcNow.ToString('o') } | ConvertTo-Json
    $tmp = "$StatePath.$PID.tmp"
    [IO.File]::WriteAllText($tmp,$data)
    if ([IO.File]::Exists($StatePath)) { [IO.File]::Replace($tmp,$StatePath,[Management.Automation.Language.NullString]::Value) } else { [IO.File]::Move($tmp,$StatePath) }
}
. (Join-Path $BundleRoot 'scripts/ChildSessionHost.ps1')
Initialize-ChildSessionHost
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
public static class LocalParentPassword {
    [ComImport, Guid("C1E6743A-41C1-4A74-832A-0DD06C1C7A0E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPassword { void PutClearTextPassword(IntPtr bstr); }
    // DISPIDs and signatures are from the inbox IMsTscAxEvents type library.
    public static void Watch(object control, int dispid, bool numeric, string file) {
        Action<string> record = delegate(string code) {
            try { File.AppendAllText(file, dispid + " " + code + Environment.NewLine); }
            catch (IOException) { }
        };
        Delegate callback;
        if (numeric) callback = new Action<int>(delegate(int code) { record(code.ToString(System.Globalization.CultureInfo.InvariantCulture)); });
        else callback = new Action(delegate() { record("-"); });
        ComEventsHelper.Combine(control, new Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6"), dispid, callback);
    }
    public static bool AddressesAreLocal(System.Net.IPAddress[] resolved, System.Net.IPAddress[] local) {
        if (resolved == null || resolved.Length == 0) return false;
        foreach (var raw in resolved) {
            var address = raw.IsIPv4MappedToIPv6 ? raw.MapToIPv4() : raw;
            if (System.Net.IPAddress.IsLoopback(address)) continue;
            bool found = false;
            foreach (var item in local) {
                var candidate = item.IsIPv4MappedToIPv6 ? item.MapToIPv4() : item;
                if (address.Equals(candidate) && !address.Equals(System.Net.IPAddress.Any) &&
                    !address.Equals(System.Net.IPAddress.IPv6Any)) { found = true; break; }
            }
            if (!found) return false;
        }
        return true;
    }
    public static void Apply(object control) {
        using (var secret = new SecureString()) {
            int c;
            while ((c = Console.In.Read()) >= 0) {
                if (c == 0xFEFF && secret.Length == 0) continue;
                if (c < 33 || c > 126 || secret.Length >= 96) throw new InvalidDataException("Invalid one-run credential input.");
                secret.AppendChar((char)c);
            }
            if (secret.Length < 40) throw new InvalidDataException("Incomplete one-run credential input.");
            IntPtr bstr = Marshal.SecureStringToBSTR(secret);
            try { ((IPassword)control).PutClearTextPassword(bstr); }
            finally { Marshal.ZeroFreeBSTR(bstr); }
        }
    }
}
'@
$phase='create-control'
$form=$null
try {
    Record-State $phase
    $form=[Windows.Forms.Form]::new()
    $form.Text='CI local test parent'
    $form.ClientSize=[Drawing.Size]::new(1024,720)
    $control=[ParallelDesktop.ChildRdpControl]::new(); $control.Dock='Fill'
    ([ComponentModel.ISupportInitialize]$control).BeginInit(); $form.Controls.Add($control); ([ComponentModel.ISupportInitialize]$control).EndInit()
    $form.Show(); [Windows.Forms.Application]::DoEvents()
    $ocx=$control.GetOcx(); $phase='verify-local-machine-target'; Record-State $phase
    $machine=[Environment]::MachineName
    if (!$machine -or $machine -ine [Net.Dns]::GetHostName()) { throw 'Local machine names disagree.' }
    $local=@(foreach ($nic in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
        foreach ($item in $nic.GetIPProperties().UnicastAddresses) { $item.Address }
    })
    $resolved=[Net.Dns]::GetHostAddresses($machine)
    if (![LocalParentPassword]::AddressesAreLocal($resolved,$local)) { throw 'Target does not resolve exclusively to local addresses.' }
    $checkedAddressCount=$resolved.Length
    $phase='configure-machine-rdp'; Record-State $phase
    $eventHooks=[ordered]@{}
    foreach ($id in @(1,2,3,4,10,18,19,22)) {
        try { [LocalParentPassword]::Watch($ocx,$id,($id -in @(4,10,22)),($StatePath+'.events')); $eventHooks[[string]$id]=$true }
        catch { $eventHooks[[string]$id]=$false }
    }
    $ocx.Server=$machine; $ocx.UserName=$TestUser; $ocx.Domain=[Environment]::MachineName
    $adv=$ocx.AdvancedSettings7; $adv.AuthenticationLevel=1; $adv.EnableCredSspSupport=$true
    if ($ocx.Server -ine $machine -or $adv.AuthenticationLevel -ne 1 -or !$adv.EnableCredSspSupport) { throw 'Strict loopback configuration did not persist.' }
    $phase='supply-ephemeral-credential'; Record-State $phase
    [LocalParentPassword]::Apply($ocx)
    $phase='ordinary-rdp-connect'; Record-State $phase
    $ocx.Connect(); $phase='ordinary-rdp-pump'; Record-State $phase
    $last=-1; $attempt=$false
    while (!$form.IsDisposed) {
        [Windows.Forms.Application]::DoEvents()
        $state=[int]$ocx.Connected
        if ($state -ne $last) { Record-State $phase $state ([int]$ocx.ExtendedDisconnectReason); $last=$state }
        if ($state -eq 0 -and $attempt) { break }
        if ($state -ne 0) { $attempt=$true }
        Start-Sleep -Milliseconds 100
    }
} catch {
    # No COM argument values or exception text are placed in evidence.
    Record-State "failed-$phase" $null ('0x{0:X8}' -f $_.Exception.GetBaseException().HResult)
    exit 2
} finally { if ($form) { $form.Dispose() } }
