using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using WindowsComputerUse.Core;

namespace WindowsComputerUse.Worker;

internal static class WindowCapture
{
    public static (CaptureInfo Info, string Image) Capture(WindowInfo window, CaptureRequest request)
    {
        CapturePolicy.Validate(request);
        if (window.Minimized) throw new DesktopException("window_minimized", "Restore the window before capturing. This backend does not claim fresh minimized-window images.");
        DesktopError? fallback = null;
        var result = CapturePolicy.Run(request.Method,
            method => method == "wgc" ? WgcCapture.Capture(window, request) : CaptureLegacy(window, request with { Method = method }),
            error => fallback = error);
        return (result.Info with { RequestedMethod = request.Method, FallbackError = fallback }, result.Image);
    }

    private static (CaptureInfo Info, string Image) CaptureLegacy(WindowInfo window, CaptureRequest request)
    {
        Native.EnsureCaptureGeometry(window);
        var bounds = window.Bounds;
        var (width, height) = CaptureGeometry.OutputSize(bounds.Width, bounds.Height, request.MaxWidth);
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Black);
            if (request.Method == "print_window")
            {
                nint dc = graphics.GetHdc();
                try
                {
                    if (!Native.PrintWindow((nint)window.Identity.Handle, dc, 2))
                        throw new DesktopException("capture_failed", "PrintWindow failed. You may explicitly request screen capture after making the target visible.");
                }
                finally { graphics.ReleaseHdc(dc); }
            }
            else
            {
                Native.EnsureForeground((nint)window.Identity.Handle, false);
                graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
                Native.EnsureForeground((nint)window.Identity.Handle, false);
            }
        }
        Native.EnsureCaptureGeometry(window);
        using var scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(bitmap, 0, 0, width, height);
        }
        using var output = new MemoryStream();
        scaled.Save(output, ImageFormat.Png);
        string[] warnings = request.Method == "print_window"
            ? ["PrintWindow may return blank or stale content for GPU-rendered apps; inspect the image before using it. Capturing does not prove business data was refreshed."]
            : ["Screen capture includes any overlapping content; inspect it before acting. Capturing does not prove business data was refreshed."];
        return (new("", window.WindowRef, DateTimeOffset.UtcNow, request.Method, bounds, width, height, warnings,
            WindowBounds: window.Bounds, RequestedMethod: request.Method), Convert.ToBase64String(output.ToArray()));
    }
}
