using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WindowsComputerUse.Core;

namespace WindowsComputerUse.Worker;

internal static class WgcCapture
{
    public static (CaptureInfo Info, string Image) Capture(WindowInfo window, CaptureRequest request)
    {
        try { return CaptureAsync(window, request).GetAwaiter().GetResult(); }
        catch (DesktopException) { throw; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or TypeLoadException or PlatformNotSupportedException)
        { throw new DesktopException("wgc_not_supported", $"Windows Graphics Capture is unavailable: {ex.Message}"); }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            if (ex.HResult == unchecked((int)0x80070005))
                throw new DesktopException("capture_access_denied", "Windows denied capture of this target. No fallback attempted.");
            throw new DesktopException("wgc_capture_failed", $"Windows Graphics Capture failed (HRESULT 0x{ex.HResult:X8}): {ex.Message}");
        }
    }

    private static async Task<(CaptureInfo Info, string Image)> CaptureAsync(WindowInfo window, CaptureRequest request)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) || !GraphicsCaptureSession.IsSupported())
            throw new DesktopException("wgc_not_supported", "This WGC backend requires Windows 10 build 19041+ and a supported graphics capture session.");
        nint hwnd = (nint)window.Identity.Handle;
        Native.EnsureCaptureGeometry(window);
        var initialFrameBounds = Native.ExtendedFrameBounds(hwnd);
        var item = WgcInterop.CreateItem(hwnd);
        var size = item.Size;
        CaptureGeometry.OutputSize(size.Width, size.Height, request.MaxWidth);
        using var device = WgcInterop.CreateDevice();
        // Free-threaded frame pools do not require a GUI dispatcher/message loop in the worker.
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        // Keep the OS capture border; no extra consent or borderless-capture permission is requested.
        session.StartCapture();
        var timer = Stopwatch.StartNew();
        Direct3D11CaptureFrame? selected = null;
        while (timer.Elapsed < TimeSpan.FromSeconds(4))
        {
            Native.EnsureCaptureGeometry(window);
            var frame = pool.TryGetNextFrame();
            if (frame is null) { await Task.Delay(15).ConfigureAwait(false); continue; }
            // This pool belongs only to this request. SystemRelativeTime is the compositor's
            // render time, not proof of a new application update or a timestamp we can require
            // to exceed StartCapture (a static application's pixels may be older).
            selected = frame;
            break;
        }
        if (selected is null) throw new DesktopException("wgc_timeout", "WGC returned no frame for this session within four seconds.", retryable: true);
        using (selected)
        {
            Native.EnsureCaptureGeometry(window);
            if (Native.ExtendedFrameBounds(hwnd) != initialFrameBounds)
                throw new DesktopException("stale_capture", "The visible window frame changed during capture. Capture again.", retryable: true);
            var content = selected.ContentSize;
            var geometry = CaptureGeometry.MatchWgcFrame(content.Width, content.Height, window.Bounds, initialFrameBounds);
            var output = CaptureGeometry.OutputSize(content.Width, content.Height, request.MaxWidth);
            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(selected.Surface, BitmapAlphaMode.Ignore);
            // A resized surface can contain undefined padding. Never encode it or guess its coordinate origin.
            if (bitmap.PixelWidth != content.Width || bitmap.PixelHeight != content.Height)
                throw new DesktopException("stale_capture", "WGC surface size changed while capturing. Capture again.", retryable: true);
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetSoftwareBitmap(bitmap);
            encoder.BitmapTransform.ScaledWidth = (uint)output.Width;
            encoder.BitmapTransform.ScaledHeight = (uint)output.Height;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            await encoder.FlushAsync();
            Native.EnsureCaptureGeometry(window);
            if (Native.ExtendedFrameBounds(hwnd) != initialFrameBounds)
                throw new DesktopException("stale_capture", "The visible window frame changed while encoding. Capture again.", retryable: true);
            using var input = stream.GetInputStreamAt(0);
            using var reader = new DataReader(input);
            uint length = checked((uint)stream.Size);
            uint loaded = await reader.LoadAsync(length);
            if (loaded != length) throw new DesktopException("wgc_capture_failed", "Could not read the complete encoded PNG.");
            byte[] png = new byte[checked((int)length)];
            reader.ReadBytes(png);
            var metadata = new CaptureInfo("", window.WindowRef, DateTimeOffset.UtcNow, "wgc", geometry.Bounds,
                output.Width, output.Height,
                ["This session's capture frame does not prove business data refreshed. This is SDR 8-bit capture; HDR colors may differ."],
                window.Bounds, geometry.Source, selected.SystemRelativeTime.Ticks, request.Method);
            return (metadata, Convert.ToBase64String(png));
        }
    }
}
