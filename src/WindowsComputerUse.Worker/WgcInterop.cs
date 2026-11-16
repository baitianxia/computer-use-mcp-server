using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WindowsComputerUse.Core;
using WinRT;

namespace WindowsComputerUse.Worker;

/// <summary>Small native bridge only; capture/encoding use the Windows SDK's WinRT projection.</summary>
internal static class WgcInterop
{
    [ComImport, Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(nint window, in Guid iid, out nint item);
        [PreserveSig] int CreateForMonitor(nint monitor, in Guid iid, out nint item);
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WindowsCreateString(string source, int length, out nint value);
    [DllImport("combase.dll", ExactSpelling = true)] private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll", ExactSpelling = true)] private static extern int RoGetActivationFactory(nint classId, in Guid iid, out nint factory);
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags,
        nint featureLevels, uint featureLevelCount, uint sdkVersion, out nint device, out int selectedLevel, out nint context);
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    public static GraphicsCaptureItem CreateItem(nint window)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        nint name = 0, factory = 0, item = 0;
        object? factoryObject = null;
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out name));
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(name, typeof(IGraphicsCaptureItemInterop).GUID, out factory));
            factoryObject = Marshal.GetObjectForIUnknown(factory);
            var interop = (IGraphicsCaptureItemInterop)factoryObject;
            var itemIid = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760");
            Marshal.ThrowExceptionForHR(interop.CreateForWindow(window, itemIid, out item));
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(item);
        }
        finally
        {
            if (item != 0) Marshal.Release(item);
            if (factoryObject is not null) Marshal.ReleaseComObject(factoryObject);
            if (factory != 0) Marshal.Release(factory);
            if (name != 0) WindowsDeleteString(name);
        }
    }

    public static IDirect3DDevice CreateDevice()
    {
        nint device = 0, context = 0, dxgi = 0, winrt = 0;
        try
        {
            int hr = D3D11CreateDevice(0, 1 /* hardware */, 0, 0x20 /* BGRA */, 0, 0, 7, out device, out _, out context);
            if (hr < 0)
            {
                if (context != 0) { Marshal.Release(context); context = 0; }
                if (device != 0) { Marshal.Release(device); device = 0; }
                hr = D3D11CreateDevice(0, 5 /* WARP */, 0, 0x20, 0, 0, 7, out device, out _, out context);
            }
            if (hr < 0) throw new DesktopException("wgc_device_failed", $"D3D11 device creation failed (HRESULT 0x{hr:X8}).");
            var iid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in iid, out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out winrt));
            return MarshalInterface<IDirect3DDevice>.FromAbi(winrt);
        }
        finally
        {
            if (winrt != 0) Marshal.Release(winrt);
            if (dxgi != 0) Marshal.Release(dxgi);
            if (context != 0) Marshal.Release(context);
            if (device != 0) Marshal.Release(device);
        }
    }
}
