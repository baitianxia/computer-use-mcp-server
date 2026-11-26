param([Parameter(Mandatory)][string]$BundleRoot)
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or
    [Threading.Thread]::CurrentThread.GetApartmentState() -ne [Threading.ApartmentState]::STA) {
    throw 'Run this check in Windows PowerShell with -STA.'
}

. (Join-Path $BundleRoot 'scripts/ChildSessionHost.ps1')
Initialize-ChildSessionHost
$form = [Windows.Forms.Form]::new()
try {
    $form.Text = 'Computer Use - RDP configuration test'
    $form.ShowInTaskbar = $false
    # The OCX defaults to the control size, so use a different size from the requested desktop.
    $form.ClientSize = [Drawing.Size]::new(900, 600)
    $control = [ParallelDesktop.ChildRdpControl]::new()
    $control.Dock = 'Fill'
    ([ComponentModel.ISupportInitialize]$control).BeginInit()
    $form.Controls.Add($control)
    ([ComponentModel.ISupportInitialize]$control).EndInit()
    $form.Show()
    [Windows.Forms.Application]::DoEvents()

    $control.ConfigureLocalChild(1024, 660)
    if ($control.ConnectionState() -ne 0) { throw 'Configuration unexpectedly started an RDP connection.' }
    $ocx = $control.GetOcx()
    if ($ocx.Server -ne 'localhost' -or $ocx.DesktopWidth -ne 1024 -or $ocx.DesktopHeight -ne 660) {
        throw "Unexpected RDP settings: server=$($ocx.Server), width=$($ocx.DesktopWidth), height=$($ocx.DesktopHeight)."
    }
    if ($ocx.AdvancedSettings7.EnableCredSspSupport -ne $true) { throw 'RDP CredSSP support was not enabled.' }
    Write-Output "RDP child-session configuration passed on control $($ocx.Version) (disconnected)."
} finally {
    $form.Dispose()
}
