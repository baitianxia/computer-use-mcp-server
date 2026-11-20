using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsComputerUse.Core;

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Json);
}

public sealed record WorkerRequest(string Id, string Operation, JsonElement Arguments);
public sealed record WorkerResponse(string Id, JsonElement? Data = null, DesktopError? Error = null, string? ImageBase64 = null);
public sealed record DesktopError(string Code, string Message, string Execution = "not_executed", bool Retryable = false);
public sealed class DesktopException(string code, string message, string execution = "not_executed", bool retryable = false) : Exception(message)
{
    public DesktopError Error { get; } = new(code, message, execution, retryable);
}

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= X && y >= Y && x < (long)X + Width && y < (long)Y + Height;
}

public sealed record WindowIdentity(long Handle, int ProcessId, long ProcessStartedUtcTicks);
public sealed record WindowInfo(string WindowRef, WindowIdentity Identity, string Title, string ProcessName,
    string? ExecutablePath, long OwnerHandle, PixelRect Bounds, bool Minimized, bool Foreground,
    string ClassName = "", uint Dpi = 96);
public sealed record ElementInfo(string ElementRef, string? ParentRef, int Depth, string Name, string AutomationId,
    string ControlType, string ClassName, bool Enabled, bool Offscreen, PixelRect Bounds,
    string[] Patterns, string? Value = null, string? Text = null, ElementState? State = null);
public sealed record ElementState(string? Toggle = null, bool? Selected = null, string? ExpandCollapse = null,
    bool? ReadOnly = null, double? RangeValue = null, double? RangeMinimum = null, double? RangeMaximum = null,
    double? HorizontalScrollPercent = null, double? VerticalScrollPercent = null);
public sealed record Observation(string ObservationId, string WindowRef, DateTimeOffset ObservedAt,
    WindowInfo Window, IReadOnlyList<ElementInfo> Elements, bool Truncated, string[] Warnings,
    int ScannedNodes = 0, bool Filtered = false);
public sealed record CaptureInfo(string ObservationId, string WindowRef, DateTimeOffset CapturedAt, string Method,
    PixelRect ScreenBounds, int ImageWidth, int ImageHeight, string[] Warnings,
    PixelRect? WindowBounds = null, string BoundsSource = "window_rect", long? FrameSystemRelativeTimeTicks = null,
    string? RequestedMethod = null, DesktopError? FallbackError = null);

public sealed record WindowSelector(string? ProcessName = null, string? TitleContains = null, string? ExecutablePath = null,
    string? ClassName = null, int? ProcessId = null);
public sealed record ElementSelector(string? AutomationId = null, string? Name = null, string? NameContains = null,
    string? ControlType = null, string? ClassName = null);
public sealed record ListRequest(string? Query = null, int Limit = 50, WindowSelector? Selector = null);
public sealed record InspectRequest(string WindowRef, int MaxDepth = 5, int MaxNodes = 150, ElementSelector? Selector = null);
public sealed record CaptureRequest(string WindowRef, string Method = "auto", int MaxWidth = 1600);
public sealed record ActionRequest(
    string WindowRef,
    string Action,
    string Mode = "foreground_allowed",
    string? ObservationId = null,
    string? ElementRef = null,
    string? Text = null,
    string[]? Keys = null,
    int? X = null,
    int? Y = null,
    int? ToX = null,
    int? ToY = null,
    int ScrollDelta = -120,
    string Button = "left",
    int ObserveDelayMs = 200,
    double? Number = null,
    string Horizontal = "no_amount",
    string Vertical = "no_amount");
public sealed record ActionResult(string ActionId, string Execution, string Verification,
    string Method, bool ForegroundChanged, Observation? Observation, string[] Warnings);
public sealed record WaitRequest(string WindowRef, string? Text = null, int TimeoutMs = 5000, bool Exact = false,
    string Condition = "text", ElementSelector? Selector = null, int MaxDepth = 8, int MaxNodes = 300);

public static class ActionRules
{
    public static readonly HashSet<string> PatternActions = ["invoke", "set_value", "toggle", "select", "add_selection", "remove_selection", "expand", "collapse", "set_range_value", "scroll_pattern", "scroll_into_view"];
    public static readonly HashSet<string> InputActions = ["click", "double_click", "move", "drag", "scroll", "type_text", "press_keys", "activate"];

    public static void Validate(ActionRequest request)
    {
        if (!PatternActions.Contains(request.Action) && !InputActions.Contains(request.Action))
            throw new DesktopException("invalid_action", "Unknown action. See perform_action tool description for supported actions.");
        if (request.Mode is not ("background_only" or "foreground_allowed"))
            throw new DesktopException("invalid_mode", "Mode must be background_only or foreground_allowed.");
        if (request.Mode == "background_only" && InputActions.Contains(request.Action))
            throw new DesktopException("foreground_required", "This action uses foreground input. Choose a supported UIA pattern or explicitly allow foreground operation.");
        if (PatternActions.Contains(request.Action) && (request.ElementRef is null || request.ObservationId is null))
            throw new DesktopException("element_required", "UIA actions require elementRef and observationId from inspect_window.");
        if (request.Action is "set_value" or "type_text" && request.Text is null)
            throw new DesktopException("text_required", "Supply text; an empty string clears a ValuePattern control.");
        if (request.Text?.Length > 100_000)
            throw new DesktopException("invalid_argument", "Text exceeds the 100000-character call limit.");
        if (request.Action == "press_keys" && (request.Keys is null || request.Keys.Length == 0 || request.Keys.Length > 8))
            throw new DesktopException("keys_required", "Supply 1 to 8 key names for one chord.");
        if (request.Action is "click" or "double_click" or "move" or "drag" or "scroll")
        {
            if (request.ObservationId is null)
                throw new DesktopException("observation_required", "Pointer actions require a current observationId.");
            if (request.ElementRef is null && (request.X is null || request.Y is null))
                throw new DesktopException("target_required", "Use elementRef from inspect_window, or image pixel coordinates x/y from capture_window.");
        }
        if (request.Action == "drag" && (request.ToX is null || request.ToY is null || request.ElementRef is not null))
            throw new DesktopException("invalid_argument", "Drag requires capture coordinates x/y/toX/toY; elementRef is not supported for drag.");
        if (request.Button is not ("left" or "right" or "middle"))
            throw new DesktopException("invalid_argument", "Button must be left, right or middle.");
        if (request.ObserveDelayMs is < 0 or > 3000)
            throw new DesktopException("invalid_argument", "observeDelayMs must be 0..3000.");
        if (request.ScrollDelta is < -12000 or > 12000)
            throw new DesktopException("invalid_argument", "scrollDelta must be -12000..12000.");
        if (request.Action == "set_range_value" && (request.Number is null || !double.IsFinite(request.Number.Value)))
            throw new DesktopException("invalid_argument", "set_range_value requires a finite number.");
        if (request.Action == "scroll_pattern")
        {
            string[] amounts = ["no_amount", "small_increment", "small_decrement", "large_increment", "large_decrement"];
            if (!amounts.Contains(request.Horizontal) || !amounts.Contains(request.Vertical)
                || (request.Horizontal == "no_amount" && request.Vertical == "no_amount"))
                throw new DesktopException("invalid_argument", "Specify a UIA scroll amount on at least one axis.");
        }
    }
}

public static class Coordinates
{
    public static (int X, int Y) FromImage(CaptureInfo capture, int x, int y)
    {
        if (capture.ImageWidth <= 0 || capture.ImageHeight <= 0 || capture.ScreenBounds.Width <= 0 || capture.ScreenBounds.Height <= 0
            || x < 0 || y < 0 || x >= capture.ImageWidth || y >= capture.ImageHeight)
            throw new DesktopException("invalid_coordinates", "Coordinates must lie inside the returned screenshot, measured in image pixels.");
        return (capture.ScreenBounds.X + (int)((long)x * capture.ScreenBounds.Width / capture.ImageWidth),
            capture.ScreenBounds.Y + (int)((long)y * capture.ScreenBounds.Height / capture.ImageHeight));
    }
}
