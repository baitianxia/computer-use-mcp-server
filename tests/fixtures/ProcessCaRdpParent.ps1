# CI-only outer client. The package's child-session implementation remains the inner probe.
function Initialize-ProcessCaClient {
    if ('ProcessCaRdpClient' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Text;
using System.Security;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public sealed class ProcessCaRdpClient : IDisposable {
    public Process Client { get; private set; }
    private readonly HashSet<string> observed = new HashSet<string>();
    private readonly ManualResetEventSlim stdoutDone = new ManualResetEventSlim();
    private readonly ManualResetEventSlim stderrDone = new ManualResetEventSlim();

    // A fixed destination keeps all sockets on loopback. Verify the TLS/NLA name is also local.
    public static bool AddressesAreLocal(IPAddress[] resolved, IPAddress[] owned) {
        if (resolved == null || resolved.Length == 0) return false;
        foreach (var raw in resolved) {
            var a = raw.IsIPv4MappedToIPv6 ? raw.MapToIPv4() : raw;
            if (IPAddress.IsLoopback(a)) continue;
            bool found = false;
            foreach (var rawLocal in owned) {
                var l = rawLocal.IsIPv4MappedToIPv6 ? rawLocal.MapToIPv4() : rawLocal;
                if (a.Equals(l) && !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any)) { found = true; break; }
            }
            if (!found) return false;
        }
        return true;
    }
    public static string VerifiedLocalMachineName() {
        string machine = Environment.MachineName;
        if (!System.Text.RegularExpressions.Regex.IsMatch(machine, @"^[a-zA-Z0-9][a-zA-Z0-9.-]{0,252}$") ||
            !String.Equals(machine,Dns.GetHostName(),StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Machine name does not match the OS host name.");
        var owned = new List<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses) owned.Add(unicast.Address);
        if (!AddressesAreLocal(Dns.GetHostAddresses(machine),owned.ToArray())) throw new InvalidOperationException("The host name does not resolve exclusively to local addresses.");
        return machine;
    }
    public static string Classify(string line) {
        // Upstream libfreerdp/crypto/tls.c emits these at the trust decision itself.
        if (line.IndexOf("certificate not trusted, aborting.",StringComparison.OrdinalIgnoreCase)>=0) return "tls-certificate-rejected";
        if (line.IndexOf("No certificate stored, automatically denying.",StringComparison.OrdinalIgnoreCase)>=0) return "strict-deny";
        if (line.IndexOf("automatically accepting",StringComparison.OrdinalIgnoreCase)>=0 ||
            line.IndexOf("certificate ignored by configuration",StringComparison.OrdinalIgnoreCase)>=0 ||
            line.IndexOf("certificate manually accepted by configuration",StringComparison.OrdinalIgnoreCase)>=0 ||
            line.IndexOf("Certificate not checked",StringComparison.OrdinalIgnoreCase)>=0) return "unsafe-trust-decision";
        if (line.IndexOf("self-signed certificate",StringComparison.OrdinalIgnoreCase)>=0 ||
            line.IndexOf("self signed certificate",StringComparison.OrdinalIgnoreCase)>=0) return "self-signed";
        if (line.IndexOf("ERRCONNECT_AUTHENTICATION_FAILED",StringComparison.Ordinal)>=0) return "authentication-failed";
        if (line.IndexOf("ERRCONNECT_TLS_CONNECT_FAILED",StringComparison.Ordinal)>=0) return "tls-failed";
        if (line.IndexOf("ERRCONNECT_CONNECT_TRANSPORT_FAILED",StringComparison.Ordinal)>=0) return "transport-failed";
        if (line.IndexOf("ERRCONNECT_LOGON_FAILURE",StringComparison.Ordinal)>=0) return "logon-failure";
        return null;
    }
    private void Record(string line) { string t=Classify(line); if (t!=null) lock(observed) observed.Add(t); }
    public string[] Tags() { lock(observed) { var a=new string[observed.Count]; observed.CopyTo(a); Array.Sort(a); return a; } }
    public void Drain() { stdoutDone.Wait(1000); stderrDone.Wait(1000); }
    // Framework's Process.Start automatically flushes a StreamWriter using Console.InputEncoding.
    // A BOM can therefore already precede our first BaseStream.Write. Modern .NET has a
    // per-process setting; on Framework limit the change to Start() on this one test thread.
    private static readonly object inputEncodingLock = new object();
    private static bool StartWithoutPreamble(Process process) {
        var option = typeof(ProcessStartInfo).GetProperty("StandardInputEncoding");
        var noBom = new UTF8Encoding(false);
        if (option != null) {
            option.SetValue(process.StartInfo,noBom,null);
            return process.Start();
        }
        lock(inputEncodingLock) {
            var previous = Console.InputEncoding;
            if (previous.GetPreamble().Length == 0) return process.Start();
            try {
                Console.InputEncoding = noBom;
                return process.Start();
            } finally { Console.InputEncoding = previous; }
        }
    }
    public static ProcessCaRdpClient StartSentinel(ProcessStartInfo psi, bool legacy) {
        // The previous encoding behavior can only be invoked with this public constant.
        return new ProcessCaRdpClient(psi,Encoding.ASCII.GetBytes("Public-RDP-stdin-sentinel_437"),legacy);
    }
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
    private static extern bool LogonUserW(string user,string domain,IntPtr password,int type,int provider,out SafeAccessTokenHandle token);
    public static int ValidateTemporaryAccount(string user,SecureString password) {
        if(!System.Text.RegularExpressions.Regex.IsMatch(user,@"^CUrdp[0-9a-f]{12}$")) throw new ArgumentException("A generated local test account is required.");
        IntPtr raw = Marshal.SecureStringToGlobalAllocUnicode(password);
        try {
            SafeAccessTokenHandle token;
            bool ok = LogonUserW(user,Environment.MachineName,raw,3,0,out token);
            int error = ok ? 0 : Marshal.GetLastWin32Error();
            if (token!=null) token.Dispose();
            return error;
        } finally { Marshal.ZeroFreeGlobalAllocUnicode(raw); }
    }
    public ProcessCaRdpClient(ProcessStartInfo psi,byte[] password) : this(psi,password,false) { }
    private ProcessCaRdpClient(ProcessStartInfo psi,byte[] password,bool legacySentinel) {
        Client=new Process(); Client.StartInfo=psi;
        Client.OutputDataReceived += delegate(object s,DataReceivedEventArgs e) { if(e.Data==null) stdoutDone.Set(); else Record(e.Data); };
        Client.ErrorDataReceived += delegate(object s,DataReceivedEventArgs e) { if(e.Data==null) stderrDone.Set(); else Record(e.Data); };
        try {
            if(!(legacySentinel ? Client.Start() : StartWithoutPreamble(Client))) throw new IOException("Local RDP client did not start.");
            Client.BeginOutputReadLine(); Client.BeginErrorReadLine();
            using (var stream=Client.StandardInput.BaseStream) {
                stream.Write(password,0,password.Length); stream.WriteByte(10); stream.Flush();
            }
        } catch { Dispose(); throw; }
    }
    public void Dispose() {
        try { if (Client!=null && !Client.HasExited) { Client.Kill(); Client.WaitForExit(5000); } }
        catch (InvalidOperationException) { }
        finally { if(Client!=null) { Client.Dispose(); Client=null; } }
    }
}
'@
}

function New-ProcessCaStartInfo([string]$Exe,[string]$ArgsLine,[string]$WorkingDirectory) {
    $p=[Diagnostics.ProcessStartInfo]::new($exe,$argsLine)
    $p.UseShellExecute=$false; $p.RedirectStandardInput=$true; $p.RedirectStandardOutput=$true; $p.RedirectStandardError=$true
    $p.WorkingDirectory=$WorkingDirectory
    $p.EnvironmentVariables.Clear()
    foreach ($name in @('SystemRoot','SystemDrive','WINDIR','TEMP','TMP','USERPROFILE','APPDATA','LOCALAPPDATA','ProgramData','COMSPEC','USERNAME','USERDOMAIN')) {
        $value=[Environment]::GetEnvironmentVariable($name)
        if ($value) { $p.EnvironmentVariables[$name]=$value }
    }
    $p.EnvironmentVariables['PATH']=[IO.Path]::GetDirectoryName($exe)+';'+[Environment]::SystemDirectory
    return $p
}

function Test-ProcessCaStdin([string]$ReaderExe,[string]$WorkingDirectory) {
    # Always compare at the child end using raw bytes. The child never sees a credential.
    $reader={
        $expected=[Text.Encoding]::ASCII.GetBytes("Public-RDP-stdin-sentinel_437`n")
        $raw=[Console]::OpenStandardInput()
        $received=[System.Collections.Generic.List[byte]]::new()
        while (($b=$raw.ReadByte()) -ge 0) {
            $received.Add([byte]$b)
            if ($received.Count -gt ($expected.Length+8)) { exit 43 }
        }
        $actual=[BitConverter]::ToString($received.ToArray())
        $want=[BitConverter]::ToString($expected)
        if ($actual -ceq $want) { exit 0 }
        if ($actual -ceq ('EF-BB-BF-'+$want)) { exit 41 }
        if ($actual -ceq ('FF-FE-'+$want) -or $actual -ceq ('FE-FF-'+$want)) { exit 42 }
        exit 43
    }
    $fixedCommand=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($reader.ToString()))
    $result=@{ hostInputCodePage=[Console]::InputEncoding.CodePage; hostPreambleLength=[Console]::InputEncoding.GetPreamble().Length }
    foreach ($mode in @('previous','current')) {
        $p=New-ProcessCaStartInfo $ReaderExe ('-NoProfile -NonInteractive -EncodedCommand '+$fixedCommand) $WorkingDirectory
        $probe=$null
        try {
            $probe=[ProcessCaRdpClient]::StartSentinel($p,($mode -eq 'previous'))
            $result[$mode]=if (!$probe.Client.WaitForExit(10000)) { 'timed-out' } else {
                switch ($probe.Client.ExitCode) { 0 {'exact'} 41 {'utf8-bom-prefixed'} 42 {'utf16-bom-prefixed'} 43 {'different-bytes'} default {'reader-error'} }
            }
        } finally { if ($probe) { $probe.Dispose() } }
    }
    $result
}

function Start-ProcessCaClient([string]$Machine,[string]$User,[string]$Pem,[string]$EmptyCaDir,[byte[]]$Password) {
    $exe='C:\msys64\ucrt64\bin\sdl-freerdp.exe'
    # The workflow installs this file from its signed MSYS2 package before entering the fixture.
    if (![IO.File]::Exists($exe)) { throw 'The signed-package SDL client was not installed at its fixture path.' }
    if ($Machine -cne [ProcessCaRdpClient]::VerifiedLocalMachineName() -or $User -notmatch '^CU(?:rdp|neg)[0-9a-f]{12}$') { throw 'Unexpected local RDP target or fixture account.' }
    if (![IO.File]::Exists($Pem) -or ![IO.Directory]::Exists($EmptyCaDir) -or [IO.Directory]::GetFileSystemEntries($EmptyCaDir).Length) { throw 'The process CA files are incomplete or the auxiliary CA directory is not empty.' }
    $argsLine='/v:127.0.0.1:3389 /server-name:{0} /u:{1} /d:{0} /sec:nla /cert:deny /proxy:ignore /from-stdin:force /log-level:INFO' -f $Machine,$User
    $p=New-ProcessCaStartInfo $exe $argsLine ([IO.Path]::GetDirectoryName($Pem))
    $p.EnvironmentVariables['SSL_CERT_FILE']=$Pem; $p.EnvironmentVariables['SSL_CERT_DIR']=$EmptyCaDir
    $p.EnvironmentVariables['WLOG_APPENDER']='CONSOLE'
    return [ProcessCaRdpClient]::new($p,$Password)
}

function Test-ProcessCaRejection([string]$Machine,[string]$Tag,[string]$Pem,[string]$EmptyCaDir) {
    $fakeUser='CUneg'+$Tag
    if (Get-LocalUser -Name $fakeUser -ErrorAction SilentlyContinue) { throw 'Negative control name already exists.' }
    $probe=$null
    try {
        $invalid=[Text.Encoding]::ASCII.GetBytes('invalid-negative-control')
        $probe=Start-ProcessCaClient $Machine $fakeUser $Pem $EmptyCaDir $invalid
        $exited=$probe.Client.WaitForExit(20000)
        if ($exited) { $probe.Drain() }
        $tags=@($probe.Tags())
        $code=if ($exited) { $probe.Client.ExitCode } else { $null }
        # A TLS / transport failure, a timeout, or a subsequent logon denial alone cannot pass.
        $confirmed=$exited -and ($code -ne 0) -and ($tags -contains 'tls-certificate-rejected') -and ($tags -contains 'strict-deny') -and ($tags -notcontains 'unsafe-trust-decision')
        return @{ exited=$exited; exitCode=$code; tags=$tags; confirmed=$confirmed }
    } finally { if ($probe) { $probe.Dispose() } }
}
