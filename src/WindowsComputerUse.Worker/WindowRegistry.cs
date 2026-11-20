using System.Diagnostics;
using System.Text;
using WindowsComputerUse.Core;

namespace WindowsComputerUse.Worker;

internal sealed class WindowRegistry
{
    private readonly Dictionary<string, WindowIdentity> references = [];

    public IReadOnlyList<WindowInfo> List(ListRequest request)
    {
        if (request.Limit is < 1 or > 200) throw new DesktopException("invalid_argument", "limit must be 1..200.");
        var result = new List<WindowInfo>();
        var live = new HashSet<WindowIdentity>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            if (Native.DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            try
            {
                var info = Describe(hwnd);
                live.Add(info.Identity);
                if (string.IsNullOrWhiteSpace(info.Title)) return true;
                if (request.Query is not null && !new[] { info.Title, info.ProcessName, info.ExecutablePath ?? "" }
                    .Any(s => s.Contains(request.Query, StringComparison.OrdinalIgnoreCase))) return true;
                if (!Selectors.Matches(info, request.Selector)) return true;
                if (result.Count < request.Limit) result.Add(info);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or DesktopException) { }
            return true;
        }, 0);
        foreach (var key in references.Where(p => !live.Contains(p.Value)).Select(p => p.Key).ToArray()) references.Remove(key);
        return result;
    }

    public WindowInfo Resolve(string reference)
    {
        if (!references.TryGetValue(reference, out var identity) || !Native.IsWindow((nint)identity.Handle))
            throw new DesktopException("stale_window", "Window reference is no longer valid. Run list_windows and select the window again.");
        var current = Describe((nint)identity.Handle);
        if (current.Identity != identity || identity.ProcessStartedUtcTicks == 0)
            throw new DesktopException("stale_window", "Process identity changed or cannot be verified. Re-list windows; do not reuse the old handle.");
        return current;
    }

    private WindowInfo Describe(nint hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || !Native.GetWindowRect(hwnd, out var rect)) throw new DesktopException("stale_window", "Window disappeared.");
        var text = new StringBuilder(1024);
        Native.GetWindowText(hwnd, text, text.Capacity);
        var className = new StringBuilder(256);
        Native.GetClassName(hwnd, className, className.Capacity);
        string name = "", path = "";
        long started = 0;
        using var process = Process.GetProcessById((int)pid);
        try { name = process.ProcessName; } catch (System.ComponentModel.Win32Exception) { }
        try { path = process.MainModule?.FileName ?? ""; } catch (System.ComponentModel.Win32Exception) { }
        try { started = process.StartTime.ToUniversalTime().Ticks; } catch (System.ComponentModel.Win32Exception) { }
        var identity = new WindowIdentity(hwnd.ToInt64(), (int)pid, started);
        var reference = references.FirstOrDefault(p => p.Value == identity).Key;
        if (reference is null) { reference = Guid.NewGuid().ToString("N"); references[reference] = identity; }
        return new(reference, identity, text.ToString(), name, string.IsNullOrEmpty(path) ? null : path,
            Native.GetWindow(hwnd, 4).ToInt64(), rect.Pixels, Native.IsIconic(hwnd), Native.GetForegroundWindow() == hwnd,
            className.ToString(), Native.GetDpiForWindow(hwnd));
    }
}
