namespace WindowsComputerUse.Core;

public static class CapturePolicy
{
    public static void Validate(CaptureRequest request)
    {
        if (request.Method is not ("auto" or "wgc" or "print_window" or "screen"))
            throw new DesktopException("invalid_argument", "method must be auto, wgc, print_window or screen.");
        if (request.MaxWidth is < 320 or > 3840)
            throw new DesktopException("invalid_argument", "maxWidth must be 320..3840.");
    }

    // Auto never switches to a screen capture, and access/geometry failures are not hidden by fallback.
    public static T Run<T>(string method, Func<string, T> capture, Action<DesktopError> onFallback)
    {
        if (method != "auto") return capture(method);
        try { return capture("wgc"); }
        catch (DesktopException ex) when (ex.Error.Code is "wgc_not_supported" or "wgc_device_failed" or "wgc_timeout" or "wgc_capture_failed")
        {
            onFallback(ex.Error);
            try { return capture("print_window"); }
            catch (DesktopException fallback)
            {
                throw new DesktopException(fallback.Error.Code,
                    $"WGC failed ({ex.Error.Code}: {ex.Message}); PrintWindow also failed: {fallback.Message}",
                    fallback.Error.Execution, fallback.Error.Retryable);
            }
        }
    }
}

public static class CaptureGeometry
{
    public static (int Width, int Height) OutputSize(int width, int height, int maxWidth)
    {
        if (width <= 0 || height <= 0 || (long)width * height > 40_000_000)
            throw new DesktopException("capture_size", "Window capture size is empty or exceeds 40 million pixels.");
        double scale = Math.Min(1d, (double)maxWidth / width);
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    public static (PixelRect Bounds, string Source) MatchWgcFrame(int width, int height, PixelRect window, PixelRect? extendedFrame)
    {
        if (extendedFrame is { } frame && frame.Width == width && frame.Height == height)
            return (frame, "dwm_extended_frame_bounds");
        if (window.Width == width && window.Height == height) return (window, "window_rect");
        throw new DesktopException("capture_geometry_mismatch",
            $"WGC frame {width}x{height} does not match a known physical window rectangle. No coordinate mapping was guessed; observe again.", retryable: true);
    }

    public static bool StillMatches(CaptureInfo capture, PixelRect window, PixelRect? extendedFrame)
    {
        if (window != (capture.WindowBounds ?? capture.ScreenBounds)) return false;
        return capture.BoundsSource switch
        {
            "window_rect" => window == capture.ScreenBounds,
            "dwm_extended_frame_bounds" => extendedFrame == capture.ScreenBounds,
            _ => false
        };
    }
}
