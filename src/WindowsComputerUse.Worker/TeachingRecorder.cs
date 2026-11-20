using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json;
using WindowsComputerUse.Core;

namespace WindowsComputerUse.Worker;

/// <summary>Hook callbacks only enqueue input metadata. UIA and capture run on a separate MTA thread.</summary>
internal sealed class TeachingRecorder(WindowInfo target, RecordingRequest request) : IDisposable
{
    private sealed record InputEvent(DateTimeOffset At, string Kind, string Detail, nint Window, int? X, int? Y, bool Injected);
    private readonly ConcurrentQueue<InputEvent> queue = new();
    private readonly AutoResetEvent changed = new(false);
    private readonly ManualResetEventSlim hookReady = new(false), frameReady = new(false);
    private readonly ConcurrentQueue<string> warnings = new();
    private Thread? hookThread, samplingThread;
    private HookProc? mouseCallback, keyCallback;
    private volatile bool stopping, recording;
    private volatile string reason = "starting";
    private uint hookThreadId;
    private int eventCount, frameCount, dropped, pending;
    private string? startupError;
    private int heldButtons;
    private long lastMove;
    private readonly HashSet<uint> modifiers = [];
    private readonly DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(request.MaxSeconds);
    public string SessionId => request.SessionId;

    public void Start()
    {
        Directory.CreateDirectory(Path.Combine(request.Directory, "frames"));
        samplingThread = new Thread(Sample) { IsBackground = true, Name = "Windows CU guidance observation" };
        samplingThread.SetApartmentState(ApartmentState.MTA); samplingThread.Start();
        if (!frameReady.Wait(TimeSpan.FromSeconds(20)) || startupError is not null)
            throw new DesktopException("guidance_recording_failed", startupError ?? "Initial teaching observation timed out.");
        hookThread = new Thread(Hooks) { IsBackground = true, Name = "Windows CU input observation" };
        hookThread.SetApartmentState(ApartmentState.STA); hookThread.Start();
        if (!hookReady.Wait(TimeSpan.FromSeconds(3)) || startupError is not null)
            throw new DesktopException("guidance_recording_failed", startupError ?? "Input recorder did not start.");
    }

    public RecordingStatus Status() => new(SessionId, recording, reason, Volatile.Read(ref eventCount),
        Volatile.Read(ref frameCount), Volatile.Read(ref dropped), warnings.ToArray());

    private bool InScope(nint window)
    {
        if (window == 0 || !Native.IsWindow((nint)target.Identity.Handle)) return false;
        Native.GetWindowThreadProcessId(window, out uint pid);
        if (pid != target.Identity.ProcessId) return false;
        for (int i = 0; window != 0 && i < 16; i++, window = Native.GetWindow(window, 4))
            if (window == (nint)target.Identity.Handle) return true;
        return false;
    }

    private void Enqueue(InputEvent input)
    {
        if (stopping) return;
        if (Interlocked.Increment(ref pending) > 256)
        { Interlocked.Decrement(ref pending); Interlocked.Increment(ref dropped); return; }
        queue.Enqueue(input); changed.Set();
    }

    private void RequestStop(string why)
    {
        if (!stopping) reason = why;
        stopping = true; recording = false; changed.Set();
        if (hookThreadId != 0) PostThreadMessage(hookThreadId, 0x0012, 0, 0); // WM_QUIT
    }

    private void Hooks()
    {
        nint mouse = 0, keyboard = 0;
        try
        {
            hookThreadId = GetCurrentThreadId();
            // Create the thread message queue before Start reports readiness or a caller can post WM_QUIT.
            PeekMessage(out _, 0, 0, 0, 0);
            mouseCallback = Mouse; keyCallback = Keyboard;
            mouse = SetWindowsHookEx(14, mouseCallback, GetModuleHandle(null), 0);
            keyboard = SetWindowsHookEx(13, keyCallback, GetModuleHandle(null), 0);
            if (mouse == 0 || keyboard == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            recording = true; reason = "recording"; hookReady.Set();
            using var timer = new System.Windows.Forms.Timer { Interval = 200 };
            timer.Tick += (_, _) =>
            {
                if (DateTimeOffset.UtcNow >= deadline) RequestStop("duration_limit");
                else if (stopping) RequestStop(reason);
            };
            timer.Start();
            Application.Run();
        }
        catch (Exception ex) { startupError = ex.Message; warnings.Enqueue(ex.Message); RequestStop("hook_error"); }
        finally
        {
            if (mouse != 0) UnhookWindowsHookEx(mouse);
            if (keyboard != 0) UnhookWindowsHookEx(keyboard);
            recording = false; hookReady.Set(); changed.Set();
        }
    }

    private nint Mouse(int code, nuint message, nint data)
    {
        try
        {
            if (code >= 0 && !stopping)
            {
                var m = Marshal.PtrToStructure<MouseData>(data);
                uint msg = (uint)message;
                string? kind = msg switch { 0x201 or 0x204 or 0x207 => "pointer_down", 0x202 or 0x205 or 0x208 => "pointer_up",
                    0x20A or 0x20E => "scroll", 0x200 when heldButtons != 0 => "drag_move", _ => null };
                int button = msg is 0x204 or 0x205 ? 2 : msg is 0x207 or 0x208 ? 4 : 1;
                if (msg is 0x201 or 0x204 or 0x207) heldButtons |= button;
                if (msg is 0x202 or 0x205 or 0x208) heldButtons &= ~button;
                if (kind == "drag_move" && Environment.TickCount64 - lastMove < 80) kind = null;
                if (kind is not null)
                {
                    nint window = Native.GetAncestor(Native.WindowFromPoint(m.Point), 2);
                    if (InScope(window))
                    {
                        lastMove = Environment.TickCount64;
                        string detail = msg is 0x204 or 0x205 ? "right" : msg is 0x207 or 0x208 ? "middle" : "left";
                        if (kind == "drag_move") detail = (heldButtons & 2) != 0 ? "right" : (heldButtons & 4) != 0 ? "middle" : "left";
                        if (kind == "scroll") detail = (msg == 0x20E ? "horizontal:" : "vertical:") + unchecked((short)(m.Data >> 16));
                        Enqueue(new(DateTimeOffset.UtcNow, kind, detail, window, m.Point.X, m.Point.Y, (m.Flags & 1) != 0));
                    }
                }
            }
        }
        catch { Interlocked.Increment(ref dropped); }
        return CallNextHookEx(0, code, message, data);
    }

    private nint Keyboard(int code, nuint message, nint data)
    {
        try
        {
            if (code >= 0 && !stopping)
            {
                var key = Marshal.PtrToStructure<KeyData>(data);
                bool down = (uint)message is 0x100 or 0x104;
                bool modifier = key.Key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5;
                if (modifier) { if (down) modifiers.Add(key.Key); else modifiers.Remove(key.Key); }
                bool ctrl = modifiers.Overlaps([0x11, 0xA2, 0xA3]), alt = modifiers.Overlaps([0x12, 0xA4, 0xA5]);
                if (down && key.Key == 0x7B && ctrl && alt) RequestStop("local_stop_shortcut");
                else if (down && !modifier)
                {
                    nint window = Native.GetAncestor(Native.GetForegroundWindow(), 2);
                    if (InScope(window))
                    {
                        bool command = ctrl || alt || modifiers.Overlaps([0x5B, 0x5C]) || key.Key is 0x08 or 0x09 or 0x0D or 0x1B or >= 0x21 and <= 0x2E or >= 0x70 and <= 0x87;
                        string detail = command ? (ctrl ? "CTRL+" : "") + (alt ? "ALT+" : "")
                            + (modifiers.Overlaps([0x10, 0xA0, 0xA1]) ? "SHIFT+" : "")
                            + (modifiers.Overlaps([0x5B, 0x5C]) ? "WIN+" : "") + ((Keys)key.Key).ToString() : "text input; read resulting UIA value or ask user";
                        Enqueue(new(DateTimeOffset.UtcNow, command ? "key" : "text_input", detail, window, null, null, (key.Flags & 0x10) != 0));
                    }
                }
            }
        }
        catch { Interlocked.Increment(ref dropped); }
        return CallNextHookEx(0, code, message, data);
    }

    private void Sample()
    {
        try
        {
            using var desktop = new DesktopSession();
            var previous = new Dictionary<nint, GuidanceFrame>();
            GuidanceFrame Capture(nint handle)
            {
                var original = desktop.Describe((nint)target.Identity.Handle);
                if (original.Identity != target.Identity) throw new DesktopException("stale_window", "Guidance target identity changed.");
                var window = desktop.Describe(handle);
                string id = $"demo-{Interlocked.Increment(ref frameCount):D4}";
                return GuidanceFiles.Snapshot(request.Directory, id,
                    () => desktop.Inspect(new(window.WindowRef, 8, 200)),
                    () => desktop.Capture(new(window.WindowRef, MaxWidth: 1200)));
            }
            var first = Capture((nint)target.Identity.Handle); previous[(nint)target.Identity.Handle] = first;
            frameReady.Set();
            using var stream = new FileStream(Path.Combine(request.Directory, "events.jsonl"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            while (!stopping || !queue.IsEmpty)
            {
                changed.WaitOne(250);
                if (!stopping && !queue.IsEmpty) Thread.Sleep(150); // Coalesce one short human gesture.
                var batch = new List<InputEvent>();
                while (batch.Count < 64 && queue.TryDequeue(out var input)) { Interlocked.Decrement(ref pending); batch.Add(input); }
                if (batch.Count == 0) continue;
                var after = new Dictionary<nint, GuidanceFrame>();
                foreach (nint handle in batch.Select(e => e.Window).Append(Native.GetForegroundWindow()).Where(InScope).Distinct().Take(3))
                {
                    // Preserve input that already happened, but never schedule another screenshot after stop.
                    if (stopping) break;
                    if (frameCount >= 60) { RequestStop("frame_limit"); break; }
                    if (InScope(handle))
                    {
                        try { after[handle] = Capture(handle); }
                        catch (Exception ex) { if (warnings.Count < 10) warnings.Enqueue(ex.Message); }
                    }
                }
                int written = 0;
                foreach (var input in batch)
                {
                    previous.TryGetValue(input.Window, out var before); after.TryGetValue(input.Window, out var next);
                    ElementInfo? element = input.X.HasValue && before?.Observation is { } obs
                        && input.At - obs.ObservedAt <= TimeSpan.FromSeconds(10)
                        ? obs.Elements.Where(e => !e.Offscreen && e.Bounds.Contains(input.X.Value, input.Y!.Value))
                            .OrderByDescending(e => e.Depth).ThenBy(e => (long)e.Bounds.Width * e.Bounds.Height).FirstOrDefault() : null;
                    var entry = new GuidanceEvent(Interlocked.Increment(ref eventCount), input.At, input.Kind, input.Detail,
                        input.Window.ToInt64(), input.X, input.Y, input.Injected, before?.Id, next?.Id,
                        element is null ? null : new(AutomationId: string.IsNullOrEmpty(element.AutomationId) ? null : element.AutomationId,
                            Name: string.IsNullOrEmpty(element.Name) ? null : element.Name, ControlType: element.ControlType, ClassName: element.ClassName),
                        element is null ? "unresolved" : "prior_sample_at_point");
                    writer.WriteLine(JsonSerializer.Serialize(entry, Wire.Json));
                    written++;
                    if (eventCount >= 1000) { RequestStop("event_limit"); break; }
                }
                foreach (var frame in after) previous[frame.Key] = frame.Value;
                if (eventCount >= 1000) { Interlocked.Add(ref dropped, queue.Count + batch.Count - written); break; }
            }
        }
        catch (Exception ex) { startupError = ex.Message; warnings.Enqueue(ex.Message); RequestStop("observation_error"); }
        finally
        {
            frameReady.Set();
            try { GuidanceFiles.Write(Path.Combine(request.Directory, "recording-status.json"), Status()); }
            catch (Exception ex) { Console.Error.WriteLine("Guidance status write failed: " + ex.Message); }
        }
    }

    public void Stop(string why)
    {
        RequestStop(why);
        if (hookThread is { IsAlive: true } && !hookThread.Join(3000)) throw new DesktopException("guidance_stop_timeout", "Input observer did not stop.");
        if (samplingThread is { IsAlive: true } && !samplingThread.Join(12000)) throw new DesktopException("guidance_stop_timeout", "Observation worker did not stop; parent should terminate the worker.");
        GuidanceFiles.Write(Path.Combine(request.Directory, "recording-status.json"), Status());
    }
    public void Dispose() { RequestStop("disposed"); /* The parent hard timeout handles a blocked UIA provider. */ }

    private delegate nint HookProc(int code, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public Native.Point Point; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyData { public uint Key, Scan, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public Native.Point Point; public uint Private; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint SetWindowsHookEx(int hook, HookProc callback, nint module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
