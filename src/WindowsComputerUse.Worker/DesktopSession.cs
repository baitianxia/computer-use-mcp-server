using System.Diagnostics;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using FlaUI.UIA3.Converters;
using WindowsComputerUse.Core;

namespace WindowsComputerUse.Worker;

internal sealed class DesktopSession : IDisposable
{
    private sealed record ElementTarget(AutomationElement Element, string Name, string AutomationId, string Type);
    private readonly UIA3Automation automation = new();
    private readonly WindowRegistry windows = new();
    private readonly ObservationStore<ElementTarget> observations = new(TimeSpan.FromMinutes(2));
    private TeachingRecorder? recorder;

    public DesktopSession()
    {
        automation.NativeAutomation2.AutoSetFocus = 0;
        automation.ConnectionTimeout = TimeSpan.FromSeconds(1);
        automation.TransactionTimeout = TimeSpan.FromSeconds(2);
    }

    public IReadOnlyList<WindowInfo> List(ListRequest request) => windows.List(request);
    internal WindowInfo Describe(string reference) => windows.Resolve(reference);
    internal WindowInfo Describe(nint handle) => windows.Describe(handle);
    internal RecordingStatus StartGuidance(RecordingRequest request)
    {
        if (recorder is not null) throw new DesktopException("guidance_active", "Finish the previous recorder first.");
        var target = windows.Resolve(request.WindowRef);
        recorder = new TeachingRecorder(target, request);
        try { recorder.Start(); return recorder.Status(); }
        catch { recorder.Dispose(); recorder = null; throw; }
    }
    internal RecordingStatus GuidanceStatus(string id)
    {
        if (recorder is null || recorder.SessionId != id)
            return new(id, false, "recorder_not_running", 0, 0, 0, ["Recorder was stopped or the worker restarted; persisted evidence remains available."]);
        return recorder.Status();
    }
    internal RecordingStatus FinishGuidance(string id)
    {
        if (recorder is null || recorder.SessionId != id) return GuidanceStatus(id);
        recorder.Stop("user_finished");
        var status = recorder.Status(); recorder.Dispose(); recorder = null;
        return status;
    }

    public Observation Inspect(InspectRequest request)
    {
        if (request.MaxDepth is < 0 or > 12 || request.MaxNodes is < 1 or > 500)
            throw new DesktopException("invalid_argument", "maxDepth must be 0..12; maxNodes must be 1..500.");
        var window = windows.Resolve(request.WindowRef);
        var root = automation.FromHandle((nint)window.Identity.Handle);
        var walker = automation.TreeWalkerFactory.GetControlViewWalker();
        var elements = new List<ElementInfo>();
        var targets = new Dictionary<string, ElementTarget>();
        var warnings = new List<string>();
        var timer = Stopwatch.StartNew();
        bool truncated = false;
        int scanned = 0;
        void Visit(AutomationElement element, string? parentRef, int depth)
        {
            if (scanned >= request.MaxNodes || timer.Elapsed > TimeSpan.FromSeconds(6)) { truncated = true; return; }
            string id = $"e{++scanned}";
            try
            {
                var p = element.Properties;
                var rect = element.BoundingRectangle;
                string name = p.Name.ValueOrDefault ?? "", automationId = p.AutomationId.ValueOrDefault ?? "", type = p.ControlType.ValueOrDefault.ToString();
                var patterns = new List<string>();
                if (element.Patterns.Invoke.IsSupported) patterns.Add("invoke");
                if (element.Patterns.Value.IsSupported) patterns.Add("value");
                if (element.Patterns.Toggle.IsSupported) patterns.Add("toggle");
                if (element.Patterns.SelectionItem.IsSupported) patterns.Add("selection_item");
                if (element.Patterns.ExpandCollapse.IsSupported) patterns.Add("expand_collapse");
                if (element.Patterns.Scroll.IsSupported) patterns.Add("scroll");
                if (element.Patterns.ScrollItem.IsSupported) patterns.Add("scroll_item");
                if (element.Patterns.RangeValue.IsSupported) patterns.Add("range_value");
                if (element.Patterns.Text.IsSupported) patterns.Add("text");
                bool password = p.IsPassword.ValueOrDefault;
                string? value = !password && element.Patterns.Value.IsSupported ? Limit(element.Patterns.Value.Pattern.Value.ValueOrDefault) : null;
                string? text = !password && element.Patterns.Text.IsSupported ? Limit(element.Patterns.Text.Pattern.DocumentRange.GetText(2000)) : null;
                var state = new ElementState(
                    Toggle: element.Patterns.Toggle.IsSupported ? element.Patterns.Toggle.Pattern.ToggleState.Value.ToString().ToLowerInvariant() : null,
                    Selected: element.Patterns.SelectionItem.IsSupported ? element.Patterns.SelectionItem.Pattern.IsSelected.Value : null,
                    ExpandCollapse: element.Patterns.ExpandCollapse.IsSupported ? element.Patterns.ExpandCollapse.Pattern.ExpandCollapseState.Value.ToString().ToLowerInvariant() : null,
                    ReadOnly: element.Patterns.Value.IsSupported ? element.Patterns.Value.Pattern.IsReadOnly.Value :
                        element.Patterns.RangeValue.IsSupported ? element.Patterns.RangeValue.Pattern.IsReadOnly.Value : null,
                    RangeValue: element.Patterns.RangeValue.IsSupported ? element.Patterns.RangeValue.Pattern.Value.Value : null,
                    RangeMinimum: element.Patterns.RangeValue.IsSupported ? element.Patterns.RangeValue.Pattern.Minimum.Value : null,
                    RangeMaximum: element.Patterns.RangeValue.IsSupported ? element.Patterns.RangeValue.Pattern.Maximum.Value : null,
                    HorizontalScrollPercent: element.Patterns.Scroll.IsSupported ? element.Patterns.Scroll.Pattern.HorizontalScrollPercent.Value : null,
                    VerticalScrollPercent: element.Patterns.Scroll.IsSupported ? element.Patterns.Scroll.Pattern.VerticalScrollPercent.Value : null);
                var info = new ElementInfo(id, parentRef, depth, name, automationId, type, p.ClassName.ValueOrDefault ?? "",
                    p.IsEnabled.ValueOrDefault, p.IsOffscreen.ValueOrDefault, new(rect.X, rect.Y, rect.Width, rect.Height), patterns.ToArray(), value, text, state);
                if (Selectors.Matches(info, request.Selector))
                {
                    elements.Add(info);
                    targets[id] = new(element, name, automationId, type);
                }
                if (depth >= request.MaxDepth)
                {
                    if (walker.GetFirstChild(element) is not null) truncated = true;
                    return;
                }
                var child = walker.GetFirstChild(element);
                while (child is not null)
                {
                    if (scanned >= request.MaxNodes || timer.Elapsed > TimeSpan.FromSeconds(6)) { truncated = true; break; }
                    Visit(child, id, depth + 1);
                    child = walker.GetNextSibling(child);
                }
            }
            catch (Exception ex)
            {
                truncated = true;
                if (warnings.Count < 5) warnings.Add($"A UIA branch could not be fully read: {ex.GetType().Name}.");
            }
        }
        Visit(root, null, 0);
        if (elements.Count == 0 && request.Selector is null) throw new DesktopException("uia_unavailable", "No readable controls returned. Try capture_window, or inspect permissions and client accessibility.");
        string observationId = observations.Add(window.WindowRef, targets);
        return new(observationId, window.WindowRef, DateTimeOffset.UtcNow, window, elements, truncated, warnings.ToArray(), scanned, request.Selector is not null);
    }

    public (CaptureInfo Info, string Image) Capture(CaptureRequest request)
    {
        var result = WindowCapture.Capture(windows.Resolve(request.WindowRef), request);
        var id = observations.Add(request.WindowRef, capture: result.Info);
        return (result.Info with { ObservationId = id }, result.Image);
    }

    public ActionResult Act(ActionRequest request)
    {
        ActionRules.Validate(request);
        var window = windows.Resolve(request.WindowRef);
        nint before = Native.GetForegroundWindow();
        string method = ActionRules.PatternActions.Contains(request.Action) ? "uia_pattern" : "foreground_input";
        var warnings = new List<string>();
        try
        {
            if (method == "uia_pattern") Pattern(request);
            else Input(window, request);
        }
        catch (DesktopException) { throw; }
        catch (Exception ex)
        {
            throw new DesktopException("action_error", $"{ex.GetType().Name}: {ex.Message}. Re-observe before deciding whether to retry.", "unknown");
        }
        finally { observations.InvalidateWindow(request.WindowRef); }
        if (request.ObserveDelayMs > 0) Thread.Sleep(request.ObserveDelayMs);
        bool changed = before != Native.GetForegroundWindow();
        if (request.Mode == "background_only" && changed)
            warnings.Add("The target application changed foreground focus despite UIA AutoSetFocus being disabled. Background behavior is not guaranteed for this action/client.");
        Observation? observation = null;
        try { observation = Inspect(new(request.WindowRef)); }
        catch (Exception ex) { warnings.Add($"Action returned, but post-action observation failed: {ex.Message}. List windows if a new dialog appeared."); }
        return new(Guid.NewGuid().ToString("N"), "executed", "not_verified", method, changed, observation, warnings.ToArray());
    }

    public object Wait(WaitRequest request)
    {
        WaitRules.Validate(request);
        var timer = Stopwatch.StartNew();
        Observation? last = null;
        do
        {
            last = Inspect(new(request.WindowRef, request.MaxDepth, request.MaxNodes, request.Selector));
            bool matched = WaitRules.Matches(last, request);
            if (matched) return new { predicateMatched = true, businessVerification = "not_verified", observation = last };
            Thread.Sleep(200);
        } while (timer.ElapsedMilliseconds < request.TimeoutMs);
        return new { predicateMatched = false, businessVerification = "not_verified",
            reason = request.Condition == "absent" && last?.Truncated == true ? "incomplete_observation" : "timeout", observation = last };
    }

    private AutomationElement ResolveElement(ActionRequest request)
    {
        if (request.ObservationId is null || request.ElementRef is null) throw new DesktopException("element_required", "Supply observationId and elementRef.");
        var saved = observations.Element(request.WindowRef, request.ObservationId, request.ElementRef);
        var current = saved.Element;
        if (!current.IsAvailable || (current.Properties.Name.ValueOrDefault ?? "") != saved.Name
            || (current.Properties.AutomationId.ValueOrDefault ?? "") != saved.AutomationId
            || current.Properties.ControlType.ValueOrDefault.ToString() != saved.Type)
            throw new DesktopException("stale_element", "Control changed since observation. Inspect the window again.");
        if (!current.IsEnabled) throw new DesktopException("element_disabled", "Target control is disabled.");
        return current;
    }

    private void Pattern(ActionRequest request)
    {
        var element = ResolveElement(request);
        switch (request.Action)
        {
            case "invoke" when element.Patterns.Invoke.IsSupported: element.Patterns.Invoke.Pattern.Invoke(); break;
            case "set_value" when element.Patterns.Value.IsSupported:
                if (element.Patterns.Value.Pattern.IsReadOnly.Value) throw new DesktopException("read_only", "The control's ValuePattern is read-only.");
                element.Patterns.Value.Pattern.SetValue(request.Text!); break;
            case "toggle" when element.Patterns.Toggle.IsSupported: element.Patterns.Toggle.Pattern.Toggle(); break;
            case "select" when element.Patterns.SelectionItem.IsSupported: element.Patterns.SelectionItem.Pattern.Select(); break;
            case "add_selection" when element.Patterns.SelectionItem.IsSupported: element.Patterns.SelectionItem.Pattern.AddToSelection(); break;
            case "remove_selection" when element.Patterns.SelectionItem.IsSupported: element.Patterns.SelectionItem.Pattern.RemoveFromSelection(); break;
            case "expand" when element.Patterns.ExpandCollapse.IsSupported: element.Patterns.ExpandCollapse.Pattern.Expand(); break;
            case "collapse" when element.Patterns.ExpandCollapse.IsSupported: element.Patterns.ExpandCollapse.Pattern.Collapse(); break;
            case "set_range_value" when element.Patterns.RangeValue.IsSupported:
                var range = element.Patterns.RangeValue.Pattern;
                if (range.IsReadOnly.Value) throw new DesktopException("read_only", "RangeValue is read-only.");
                if (request.Number < range.Minimum.Value || request.Number > range.Maximum.Value)
                    throw new DesktopException("invalid_argument", "Number is outside the control's reported range.");
                range.SetValue(request.Number!.Value); break;
            case "scroll_pattern" when element.Patterns.Scroll.IsSupported:
                element.Patterns.Scroll.Pattern.Scroll(ScrollAmountOf(request.Horizontal), ScrollAmountOf(request.Vertical)); break;
            case "scroll_into_view" when element.Patterns.ScrollItem.IsSupported: element.Patterns.ScrollItem.Pattern.ScrollIntoView(); break;
            default: throw new DesktopException("pattern_unsupported", "This control does not expose the requested pattern. Inspect/capture again and choose a supported action.");
        }
    }

    private static ScrollAmount ScrollAmountOf(string value) => value switch
    {
        "small_increment" => ScrollAmount.SmallIncrement, "small_decrement" => ScrollAmount.SmallDecrement,
        "large_increment" => ScrollAmount.LargeIncrement, "large_decrement" => ScrollAmount.LargeDecrement,
        _ => ScrollAmount.NoAmount
    };

    private void Input(WindowInfo window, ActionRequest request)
    {
        nint hwnd = (nint)window.Identity.Handle;
        (int X, int Y)? point = null, end = null;
        AutomationElement? element = request.ElementRef is null ? null : ResolveElement(request);
        CaptureInfo? capture = null;
        if (request.Action is "click" or "double_click" or "move" or "scroll" or "drag")
        {
            if (element is not null)
            {
                if (element.IsOffscreen || !element.TryGetClickablePoint(out var clickable))
                    throw new DesktopException("element_offscreen", "The control has no visible clickable point.");
                point = (clickable.X, clickable.Y);
            }
            else
            {
                capture = observations.Capture(request.WindowRef, request.ObservationId!);
                if (!CaptureGeometry.StillMatches(capture, window.Bounds, Native.ExtendedFrameBounds(hwnd)))
                    throw new DesktopException("stale_capture", "Window or capture frame moved/resized after capture. Capture again.");
                point = Coordinates.FromImage(capture, request.X!.Value, request.Y!.Value);
                if (request.Action == "drag") end = Coordinates.FromImage(capture, request.ToX!.Value, request.ToY!.Value);
            }
        }
        // Validate keys before any activation, so invalid chords have no side effects.
        ushort[]? keys = request.Action == "press_keys" ? request.Keys!.Select(KeyCode).ToArray() : null;
        Native.EnsureInputIdle();
        Native.EnsureForeground(hwnd, true);
        if (capture is not null && !CaptureGeometry.StillMatches(capture, windows.Resolve(request.WindowRef).Bounds, Native.ExtendedFrameBounds(hwnd)))
            throw new DesktopException("stale_capture", "Window geometry changed on activation. No pointer input sent; capture again.");
        if (element is not null && request.Action is "type_text" or "press_keys") element.ToNative().SetFocus();
        Native.EnsureForeground(hwnd, false);
        if (point is { } p) Native.EnsurePoint(hwnd, p.X, p.Y);
        if (end is { } e) Native.EnsurePoint(hwnd, e.X, e.Y);
        uint down = request.Button switch { "right" => 0x0008u, "middle" => 0x0020u, _ => 0x0002u };
        uint up = down << 1;
        var inputs = new List<Native.Input>();
        if (point is { } start) inputs.Add(Native.Move(start.X, start.Y));
        switch (request.Action)
        {
            case "activate": return;
            case "move": break;
            case "click": inputs.AddRange([Native.Mouse(down), Native.Mouse(up)]); break;
            case "double_click": inputs.AddRange([Native.Mouse(down), Native.Mouse(up), Native.Mouse(down), Native.Mouse(up)]); break;
            case "scroll": inputs.Add(Native.Mouse(0x0800, unchecked((uint)request.ScrollDelta))); break;
            case "drag":
                inputs.Add(Native.Mouse(down));
                for (int i = 1; i <= 20; i++)
                    inputs.Add(Native.Move(point!.Value.X + (end!.Value.X - point.Value.X) * i / 20,
                        point.Value.Y + (end.Value.Y - point.Value.Y) * i / 20));
                inputs.Add(Native.Mouse(up));
                break;
            case "press_keys":
                inputs.AddRange(keys!.Select(k => Native.Key(k)));
                inputs.AddRange(keys!.Reverse().Select(k => Native.Key(k, true)));
                break;
            case "type_text":
                bool textSent = false;
                try
                {
                    foreach (var chunk in request.Text!.Chunk(128))
                    {
                        Native.EnsureForeground(hwnd, false);
                        Native.Send(chunk.SelectMany(c => new[] { Native.Key(c, unicode: true), Native.Key(c, up: true, unicode: true) }).ToArray());
                        textSent = true;
                    }
                }
                catch (DesktopException ex) when (textSent)
                { throw new DesktopException(ex.Error.Code, ex.Message + " Some text was already sent; inspect before retrying.", "unknown"); }
                return;
        }
        // Button/key release is in the same SendInput batch; no sleeps while inputs are held.
        Native.EnsureForeground(hwnd, false);
        Native.Send(inputs.ToArray());
    }

    private static ushort KeyCode(string name)
    {
        string key = name.Trim().ToUpperInvariant();
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) return key[0];
        if (key.StartsWith('F') && int.TryParse(key.AsSpan(1), out int number) && number is >= 1 and <= 24) return (ushort)(0x70 + number - 1);
        return key switch
        {
            "CTRL" or "CONTROL" => 0x11, "ALT" => 0x12, "SHIFT" => 0x10, "WIN" => 0x5B,
            "ENTER" or "RETURN" => 0x0D, "TAB" => 0x09, "ESC" or "ESCAPE" => 0x1B,
            "BACKSPACE" => 0x08, "DELETE" => 0x2E, "SPACE" => 0x20, "HOME" => 0x24, "END" => 0x23,
            "LEFT" => 0x25, "UP" => 0x26, "RIGHT" => 0x27, "DOWN" => 0x28, "PAGEUP" => 0x21, "PAGEDOWN" => 0x22,
            _ => throw new DesktopException("invalid_key", $"Unsupported key '{name}'. Use letters, digits, F1..F24 or named modifier/navigation keys.")
        };
    }

    private static string? Limit(string? value) => value is { Length: > 2000 } ? value[..2000] : value;
    public void Dispose() { recorder?.Dispose(); automation.Dispose(); }
}
