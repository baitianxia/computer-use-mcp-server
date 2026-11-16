using System.Runtime.InteropServices;
using System.Text;
using WindowsComputerUse.Core;

namespace WindowsComputerUse.Worker;

internal static class Native
{
    internal delegate bool EnumWindowProc(nint hwnd, nint parameter);
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; public readonly PixelRect Pixels => new(Left, Top, Right - Left, Bottom - Top); }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] internal struct Input { public uint Type; public InputUnion Data; }

    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowProc callback, nint parameter);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool PrintWindow(nint hwnd, nint dc, uint flags);
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(nint context);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out int value, int size);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out Rect value, int size);

    internal static PixelRect? ExtendedFrameBounds(nint hwnd) => DwmGetWindowAttribute(hwnd, 9, out Rect frame, Marshal.SizeOf<Rect>()) == 0
        && frame.Pixels.Width > 0 && frame.Pixels.Height > 0 ? frame.Pixels : null;

    internal static void EnsureCaptureGeometry(WindowInfo window)
    {
        nint hwnd = (nint)window.Identity.Handle;
        if (!IsWindow(hwnd)) throw new DesktopException("stale_window", "Window closed during capture.");
        if (IsIconic(hwnd)) throw new DesktopException("window_minimized", "Window was minimized during capture.");
        if (!GetWindowRect(hwnd, out var bounds) || bounds.Pixels != window.Bounds)
            throw new DesktopException("stale_capture", "Window moved or resized during capture. Capture again.", retryable: true);
    }

    internal static void EnsureForeground(nint hwnd, bool activate)
    {
        if (!IsWindowEnabled(hwnd)) throw new DesktopException("window_disabled", "The window may be blocked by a modal dialog. List windows and inspect the dialog.");
        if (activate)
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, 9);
            SetForegroundWindow(hwnd);
            for (int attempt = 0; attempt < 10 && GetForegroundWindow() != hwnd; attempt++) Thread.Sleep(30);
        }
        if (GetForegroundWindow() != hwnd)
            throw new DesktopException("foreground_not_target", "Windows did not grant focus to the target. No keyboard or pointer input was sent.");
    }

    internal static void EnsurePoint(nint hwnd, int x, int y)
    {
        if (GetAncestor(WindowFromPoint(new Point { X = x, Y = y }), 2) != hwnd)
            throw new DesktopException("target_occluded", "The point is outside the target or covered by another window. Observe again.");
    }

    internal static void EnsureInputIdle()
    {
        foreach (var key in new[] { 0x01, 0x02, 0x04, 0x10, 0x11, 0x12, 0x5B, 0x5C })
            if ((GetAsyncKeyState(key) & 0x8000) != 0)
                throw new DesktopException("input_busy", "A mouse button or modifier is currently held. Release it before retrying.");
    }

    internal static Input Mouse(uint flags, uint data = 0) => new() { Type = 0, Data = new() { Mouse = new() { Flags = flags, Data = data } } };
    internal static Input Move(int x, int y)
    {
        int left = GetSystemMetrics(76), top = GetSystemMetrics(77), width = GetSystemMetrics(78), height = GetSystemMetrics(79);
        if (width <= 1 || height <= 1 || !new PixelRect(left, top, width, height).Contains(x, y))
            throw new DesktopException("invalid_coordinates", "The point is outside the interactive virtual screen.");
        return new() { Type = 0, Data = new() { Mouse = new() { X = (int)((long)(x - left) * 65535 / (width - 1)), Y = (int)((long)(y - top) * 65535 / (height - 1)), Flags = 0x0001 | 0x8000 | 0x4000 | 0x2000 } } };
    }

    internal static Input Key(ushort key, bool up = false, bool unicode = false) => new()
    {
        Type = 1,
        Data = new() { Keyboard = new() { Key = unicode ? (ushort)0 : key, Scan = unicode ? key : (ushort)0,
            Flags = (up ? 2u : 0u) | (unicode ? 4u : 0u) | (!unicode && (key is >= 0x21 and <= 0x2E or 0x5B or 0x5C) ? 1u : 0u) } }
    };

    internal static void Send(Input[] inputs)
    {
        uint accepted = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (accepted == inputs.Length) return;
        // A partial batch may have delivered a press without its release. Cleanup never repeats a click or key press.
        var releases = new List<Input>();
        foreach (var input in inputs)
        {
            if (input.Type == 1 && (input.Data.Keyboard.Flags & 2) == 0)
            {
                var release = input;
                release.Data.Keyboard.Flags |= 2;
                releases.Add(release);
            }
            if (input.Type == 0)
            {
                uint flags = input.Data.Mouse.Flags;
                if ((flags & 2) != 0) releases.Add(Mouse(4));
                if ((flags & 8) != 0) releases.Add(Mouse(16));
                if ((flags & 32) != 0) releases.Add(Mouse(64));
            }
        }
        if (releases.Count != 0) SendInput((uint)releases.Count, releases.ToArray(), Marshal.SizeOf<Input>());
        throw new DesktopException("input_incomplete", $"Windows accepted {accepted}/{inputs.Length} input events. Key/button release was attempted. UIPI may prevent injection; the API cannot confirm the cause.", "unknown");
    }
}
