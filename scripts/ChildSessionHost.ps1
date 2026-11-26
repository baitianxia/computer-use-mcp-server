# Dot-source from the Windows PowerShell 5.1 STA parent process.
function Initialize-ChildSessionHost {
    if ($env:OS -ne 'Windows_NT') { throw 'Windows child sessions require Windows.' }
    if ('ParallelDesktop.ChildSessions' -as [type]) { return }
    Add-Type -AssemblyName System.Windows.Forms
    $references = @([Windows.Forms.Form].Assembly.Location, [Drawing.Size].Assembly.Location, [ComponentModel.Win32Exception].Assembly.Location)
    Add-Type -ReferencedAssemblies $references -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ParallelDesktop {
    public static class ChildSessions {
        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSIsChildSessionsEnabled(out bool enabled);
        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSEnableChildSessions(bool enabled);
        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSGetChildSessionId(out uint id);
        [DllImport("wtsapi32.dll", ExactSpelling = true, SetLastError = true)]
        private static extern bool WTSQuerySessionInformationW(IntPtr server, uint session,
            int infoClass, out IntPtr buffer, out int size);
        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr buffer);

        public static bool Enabled() {
            bool enabled;
            if (!WTSIsChildSessionsEnabled(out enabled)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return enabled;
        }

        public static void SetEnabled(bool enabled) {
            if (!WTSEnableChildSessions(enabled)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        public static uint ChildId() {
            uint id;
            if (!WTSGetChildSessionId(out id)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return id; // UINT_MAX means this parent session has no child.
        }

        public static bool IsDisconnected(uint id) {
            IntPtr buffer;
            int size;
            const int WTSConnectState = 8, WTSDisconnected = 4;
            if (!WTSQuerySessionInformationW(IntPtr.Zero, id, WTSConnectState, out buffer, out size))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try { return size == sizeof(int) && Marshal.ReadInt32(buffer) == WTSDisconnected; }
            finally { WTSFreeMemory(buffer); }
        }
    }

    // IMsRdpExtendedSettings is IUnknown, not the control's default IDispatch interface.
    [ComImport, Guid("302D8188-0052-4807-806A-362B628F9AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IExtendedSettings {
        [PreserveSig] int Put([MarshalAs(UnmanagedType.BStr)] string name, [In, MarshalAs(UnmanagedType.Struct)] ref object value);
    }

    public sealed class ChildRdpControl : AxHost {
        // MsRdpClient9NotSafeForScripting is the inbox desktop control on Windows 10/11.
        public ChildRdpControl() : base("8B918B82-7985-4C24-89DF-C33AD2BBFBCD") {}
        private static object Dispatch(object target, string name, BindingFlags action, params object[] args) {
            if (target == null) throw new InvalidOperationException("The RDP control did not provide an automation object.");
            return target.GetType().InvokeMember(name, action, null, target, args);
        }

        private static COMException OperationError(string operation, Exception error) {
            Exception cause = error.GetBaseException();
            string message = String.Format("RDP operation={0}, HRESULT=0x{1:X8}: {2}", operation, cause.HResult, cause.Message);
            return new COMException(message, cause.HResult);
        }

        public void ConfigureLocalChild(int width, int height) {
            string operation = "GetOcx";
            try {
                object ocx = GetOcx();
                if (ocx == null) throw new InvalidOperationException("The RDP control is not active.");
                operation = "Server=localhost";
                Dispatch(ocx, "Server", BindingFlags.PutDispProperty, "localhost");
                operation = "DesktopWidth=" + width;
                Dispatch(ocx, "DesktopWidth", BindingFlags.PutDispProperty, width);
                operation = "DesktopHeight=" + height;
                Dispatch(ocx, "DesktopHeight", BindingFlags.PutDispProperty, height);

                operation = "QueryInterface(IMsRdpExtendedSettings)";
                IExtendedSettings settings = (IExtendedSettings)ocx;
                operation = "ConnectToChildSession=true";
                object childMode = true;
                int result = settings.Put("ConnectToChildSession", ref childMode);
                if (result < 0) Marshal.ThrowExceptionForHR(result);
                if (result != 0) throw new COMException("The RDP control did not acknowledge child session mode with S_OK.", result);

                operation = "AdvancedSettings7";
                object advanced = Dispatch(ocx, "AdvancedSettings7", BindingFlags.GetProperty);
                operation = "EnableCredSspSupport=true";
                Dispatch(advanced, "EnableCredSspSupport", BindingFlags.PutDispProperty, true);
            } catch (Exception error) {
                throw OperationError(operation, error);
            }
        }

        private object DispatchControl(string name, BindingFlags action) {
            try { return Dispatch(GetOcx(), name, action); }
            catch (Exception error) { throw OperationError(name, error); }
        }

        public void ConnectLocalChild(int width, int height) {
            ConfigureLocalChild(width, height);
            DispatchControl("Connect", BindingFlags.InvokeMethod);
        }

        public int ConnectionState() { return Convert.ToInt32(DispatchControl("Connected", BindingFlags.GetProperty)); }
        public int DisconnectReason() { return Convert.ToInt32(DispatchControl("ExtendedDisconnectReason", BindingFlags.GetProperty)); }
        public void DisconnectChild() { DispatchControl("Disconnect", BindingFlags.InvokeMethod); }
    }
}
'@
}

function Get-ChildSessionSupport {
    Initialize-ChildSessionHost
    return [ParallelDesktop.ChildSessions]::Enabled()
}

function Get-ChildSessionId {
    param([Parameter(Mandatory)][ValidateSet('before-connect','await-child-id','verify-bound-child','disable-check')][string]$Stage)
    try {
        return [ParallelDesktop.ChildSessions]::ChildId()
    } catch {
        $cause = $_.Exception.GetBaseException()
        if ($cause -isnot [ComponentModel.Win32Exception]) { throw }
        $code = $cause.NativeErrorCode
        # ERROR_NOT_FOUND can precede registration. A bound session or a settings change requires a successful query.
        if ($code -eq 1168 -and $Stage -in @('before-connect','await-child-id')) { return $null }
        $message = 'WTSGetChildSessionId ({0}) failed with Win32 {1} (0x{1:X8}): {2}' -f $Stage, $code, $cause.Message
        throw [ComponentModel.Win32Exception]::new($code, $message)
    }
}

function Set-ChildSessionSupport {
    param([Parameter(Mandatory)][bool]$Enabled)
    if ((Get-ChildSessionSupport) -eq $Enabled) { return }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Changing Windows child session support requires an elevated Windows PowerShell.'
    }
    if (-not $Enabled -and (Get-ChildSessionId -Stage disable-check) -ne [uint32]::MaxValue) {
        throw 'This Windows session still has a child session. Sign out inside the child before disabling support.'
    }
    [ParallelDesktop.ChildSessions]::SetEnabled($Enabled)
    if ((Get-ChildSessionSupport) -ne $Enabled) { throw 'Windows did not apply the requested child session setting.' }
}

function Open-ChildSessionHost {
    param([ValidateRange(5,300)][int]$TimeoutSeconds = 75, [string]$Instruction)
    if (-not (Get-ChildSessionSupport)) {
        throw 'Windows child session support is disabled. See the parallel test guide for the optional administrator setup.'
    }
    if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne [Threading.ApartmentState]::STA) {
        throw 'Open the child desktop from Windows PowerShell with -STA.'
    }
    $existing = Get-ChildSessionId -Stage before-connect
    if ($null -ne $existing -and $existing -ne [uint32]::MaxValue -and -not [ParallelDesktop.ChildSessions]::IsDisconnected($existing)) {
        throw "Child session $existing is already in use. Close its other preview before starting this test."
    }
    $parentId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $form = [Windows.Forms.Form]::new()
    try {
        $form.Text = 'Computer Use - child Windows desktop'
        $form.StartPosition = 'CenterScreen'
        $form.ClientSize = [Drawing.Size]::new(1024, 720)
        $control = [ParallelDesktop.ChildRdpControl]::new()
        $control.Dock = 'Fill'
        ([ComponentModel.ISupportInitialize]$control).BeginInit()
        $form.Controls.Add($control)
        ([ComponentModel.ISupportInitialize]$control).EndInit()
        if ($Instruction) {
            $banner = [Windows.Forms.TextBox]::new()
            $banner.ReadOnly = $true
            $banner.Multiline = $true
            $banner.Height = 60
            $banner.Dock = 'Top'
            $banner.Text = $Instruction
            $form.Controls.Add($banner)
        }
        $form.Show()
        [Windows.Forms.Application]::DoEvents()
        $control.ConnectLocalChild(1024, 660)
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $state = 0
        $attemptObserved = $false
        $lastLookup = 'RDP has not reported a connection'
        do {
            [Windows.Forms.Application]::DoEvents()
            if ($form.IsDisposed) { throw 'The child desktop preview was closed.' }
            $state = $control.ConnectionState()
            if ($state -eq 1) {
                $childId = Get-ChildSessionId -Stage await-child-id
                if ($null -eq $childId) {
                    $lastLookup = 'WTSGetChildSessionId returned Win32 1168 (0x00000490)'
                } elseif ($childId -eq [uint32]::MaxValue) {
                    $lastLookup = 'WTSGetChildSessionId reported no child session (UINT_MAX)'
                } elseif ($childId -gt 0 -and $childId -ne $parentId) {
                    return [pscustomobject]@{ Form = $form; Control = $control; ParentSessionId = $parentId; ChildSessionId = $childId }
                } else {
                    $lastLookup = "WTSGetChildSessionId returned an unusable ID ($childId, parent $parentId)"
                }
            }
            if ($state -eq 0 -and $attemptObserved) {
                throw "RDP disconnected before child session readiness. ExtendedDisconnectReason=$($control.DisconnectReason()). Last observation: $lastLookup."
            }
            if ($state -ne 0) { $attemptObserved = $true }
            Start-Sleep -Milliseconds 100
        } while ($clock.Elapsed.TotalSeconds -lt $TimeoutSeconds)
        throw "Child session connection timed out after $TimeoutSeconds seconds. RDP state=$state, ExtendedDisconnectReason=$($control.DisconnectReason()). Last observation: $lastLookup."
    } catch {
        $form.Dispose()
        throw
    }
}

function Test-ChildSessionHostConnected {
    param([Parameter(Mandatory)]$Child)
    return (-not $Child.Form.IsDisposed -and $Child.Control.ConnectionState() -eq 1 -and
        (Get-ChildSessionId -Stage verify-bound-child) -eq $Child.ChildSessionId)
}

function Close-ChildSessionHost {
    param([Parameter(Mandatory)]$Child)
    if (-not $Child.Form.IsDisposed) {
        try {
            if ($Child.Control.ConnectionState() -ne 0) { $Child.Control.DisconnectChild() }
        } finally {
            $Child.Form.Dispose()
        }
    }
}
