using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using WindowsComputerUse.Core;
using WindowsComputerUse.Server;
using WindowsComputerUse.Worker;

if (args.Contains("--protocol") || args.Contains("--windows-smoke"))
{
    Environment.ExitCode = await McpProbe.Run(args, args.Contains("--windows-smoke"));
    return;
}

if (args.Contains("--fake-worker"))
{
    string? line;
    while ((line = Console.ReadLine()) is not null)
    {
        var request = JsonSerializer.Deserialize<WorkerRequest>(line, Wire.Json)!;
        if (request.Operation is "hang" or "perform_action") Thread.Sleep(Timeout.Infinite);
        Console.WriteLine(JsonSerializer.Serialize(new WorkerResponse(request.Id, Wire.Element(new { processId = Environment.ProcessId, operation = request.Operation })), Wire.Json));
    }
    return;
}

int passed = 0, failed = 0;
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
void ExpectError(string code, Action action)
{
    try { action(); throw new Exception($"Expected {code}"); }
    catch (DesktopException ex) { Assert(ex.Error.Code == code, $"Expected {code}, got {ex.Error.Code}"); }
}
void Check(string name, Action action)
{
    try { action(); Console.WriteLine($"PASS {name}"); passed++; }
    catch (Exception ex) { Console.Error.WriteLine($"FAIL {name}: {ex.Message}"); failed++; }
}
async Task CheckAsync(string name, Func<Task> action)
{
    try { await action(); Console.WriteLine($"PASS {name}"); passed++; }
    catch (Exception ex) { Console.Error.WriteLine($"FAIL {name}: {ex.Message}"); failed++; }
}

Check("background mode rejects every foreground action", () =>
{
    foreach (string action in ActionRules.InputActions)
        ExpectError("foreground_required", () => ActionRules.Validate(new("window", action, "background_only")));
});
Check("set_value permits empty text without foreground input", () => ActionRules.Validate(new("window", "set_value", "background_only", "observation", "e1", "")));
Check("pattern actions need observed references", () => ExpectError("element_required", () => ActionRules.Validate(new("window", "invoke"))));
Check("coordinates need an observation", () => ExpectError("observation_required", () => ActionRules.Validate(new("window", "click", X: 10, Y: 20))));
Check("unknown actions fail before execution", () => ExpectError("invalid_action", () => ActionRules.Validate(new("window", "invented"))));
Check("invalid wait delays rejected", () => ExpectError("invalid_argument", () => ActionRules.Validate(new("window", "activate", ObserveDelayMs: -1))));
Check("drag needs both endpoints", () => ExpectError("invalid_argument", () => ActionRules.Validate(new("window", "drag", ObservationId: "capture", X: 1, Y: 2))));
Check("image coordinates map to scaled negative-origin screen", () =>
{
    var c = new CaptureInfo("capture", "window", DateTimeOffset.UtcNow, "screen", new(-1920, -100, 1920, 1080), 960, 540, []);
    Assert(Coordinates.FromImage(c, 480, 270) == (-960, 440), "Bad DPI/scale mapping.");
    Assert(Coordinates.FromImage(c, 959, 539) == (-2, 978), "Bad edge mapping.");
    ExpectError("invalid_coordinates", () => Coordinates.FromImage(c, 960, 0));
    ExpectError("invalid_coordinates", () => Coordinates.FromImage(c, -1, 0));
});
Check("capture defaults to auto and validates backend and image limit", () =>
{
    Assert(new CaptureRequest("window").Method == "auto", "Default must prefer WGC.");
    foreach (string method in new[] { "auto", "wgc", "print_window", "screen" }) CapturePolicy.Validate(new("window", method));
    ExpectError("invalid_argument", () => CapturePolicy.Validate(new("window", "unknown")));
    ExpectError("invalid_argument", () => CapturePolicy.Validate(new("window", MaxWidth: 0)));
    ExpectError("invalid_argument", () => CapturePolicy.Validate(new("window", MaxWidth: 3841)));
});
Check("auto capture stops after successful WGC", () =>
{
    List<string> calls = [];
    string result = CapturePolicy.Run("auto", method => { calls.Add(method); return method; }, _ => throw new Exception("Unexpected fallback."));
    Assert(result == "wgc" && calls.SequenceEqual(new[] { "wgc" }), "WGC was not the sole capture backend.");
});
Check("auto fallback preserves WGC failure and never tries screen capture", () =>
{
    foreach (string code in new[] { "wgc_not_supported", "wgc_device_failed", "wgc_timeout", "wgc_capture_failed" })
    {
        List<string> calls = [];
        DesktopError? reason = null;
        string result = CapturePolicy.Run("auto", method =>
        {
            calls.Add(method);
            if (method == "wgc") throw new DesktopException(code, "Test WGC failure.");
            return method;
        }, error => reason = error);
        Assert(result == "print_window" && calls.SequenceEqual(new[] { "wgc", "print_window" }) && reason?.Code == code, "Fallback lost its cause or selected another backend.");
    }
});
Check("explicit WGC and capture geometry or access errors never fall back", () =>
{
    foreach (var (method, code) in new[] { ("wgc", "wgc_timeout"), ("auto", "capture_access_denied"), ("auto", "capture_geometry_mismatch"), ("auto", "stale_capture"), ("auto", "window_minimized") })
    {
        int calls = 0;
        ExpectError(code, () => CapturePolicy.Run<string>(method, _ => { calls++; throw new DesktopException(code, "Test error."); }, _ => throw new Exception("Unexpected fallback.")));
        Assert(calls == 1, "Error was hidden by another backend.");
    }
});
Check("failure of both capture backends retains both error causes", () =>
{
    try
    {
        CapturePolicy.Run<string>("auto", method => throw new DesktopException(method == "wgc" ? "wgc_timeout" : "capture_failed", method + " failed"), _ => { });
        throw new Exception("Expected capture failure.");
    }
    catch (DesktopException ex)
    {
        Assert(ex.Error.Code == "capture_failed" && ex.Message.Contains("wgc_timeout") && ex.Message.Contains("print_window failed"), "Missing backend failure evidence.");
    }
});
Check("WGC visible-frame coordinates exclude invisible window borders", () =>
{
    var window = new PixelRect(-1928, -108, 1936, 1104);
    var frame = new PixelRect(-1920, -100, 1920, 1080);
    var geometry = CaptureGeometry.MatchWgcFrame(1920, 1080, window, frame);
    Assert(geometry.Bounds == frame && geometry.Source == "dwm_extended_frame_bounds", "Wrong capture origin.");
    var capture = new CaptureInfo("obs", "window", DateTimeOffset.UtcNow, "wgc", geometry.Bounds, 960, 540, [], window, geometry.Source, 123456, "auto");
    Assert(Coordinates.FromImage(capture, 480, 270) == (-960, 440), "WGC coordinates include invisible borders.");
    Assert(CaptureGeometry.StillMatches(capture, window, frame), "Unchanged WGC capture rejected.");
    Assert(!CaptureGeometry.StillMatches(capture, window with { X = -1900 }, frame), "Moved window accepted.");
    Assert(!CaptureGeometry.StillMatches(capture, window, frame with { Width = 1919 }), "Changed visible frame accepted.");
    Assert(!CaptureGeometry.StillMatches(capture, window, null), "Missing visible frame accepted.");
    var copy = JsonSerializer.Deserialize<CaptureInfo>(JsonSerializer.Serialize(capture, Wire.Json), Wire.Json)!;
    Assert(copy.ScreenBounds == frame && copy.WindowBounds == window && copy.FrameSystemRelativeTimeTicks == 123456 && copy.RequestedMethod == "auto", "Capture metadata did not survive MCP wire format.");
});
Check("WGC geometry accepts only matching known physical rectangles", () =>
{
    var window = new PixelRect(100, 200, 1000, 800);
    Assert(CaptureGeometry.MatchWgcFrame(1000, 800, window, null).Source == "window_rect", "Window rectangle fallback missing.");
    ExpectError("capture_geometry_mismatch", () => CaptureGeometry.MatchWgcFrame(990, 790, window, null));
    Assert(CaptureGeometry.OutputSize(3840, 2160, 1600) == (1600, 900), "Resized image aspect ratio changed.");
    Assert(CaptureGeometry.OutputSize(800, 600, 1600) == (800, 600), "Small images must not be enlarged.");
    ExpectError("capture_size", () => CaptureGeometry.OutputSize(0, 100, 1600));
    ExpectError("capture_size", () => CaptureGeometry.OutputSize(int.MaxValue, int.MaxValue, 1600));
});
Check("references cannot cross windows or observations", () =>
{
    var store = new ObservationStore<string>(TimeSpan.FromMinutes(2));
    var id = store.Add("one", new() { ["e1"] = "target" });
    Assert(store.Element("one", id, "e1") == "target", "Missing observed element.");
    ExpectError("stale_observation", () => store.Element("two", id, "e1"));
    ExpectError("stale_element", () => store.Element("one", id, "e2"));
    ExpectError("capture_required", () => store.Capture("one", id));
});
Check("old references expire and action invalidation is window scoped", () =>
{
    var time = DateTimeOffset.UtcNow;
    var store = new ObservationStore<string>(TimeSpan.FromSeconds(10), () => time);
    var one = store.Add("one", new() { ["e1"] = "one" });
    var two = store.Add("two", new() { ["e1"] = "two" });
    store.InvalidateWindow("one");
    ExpectError("stale_observation", () => store.Element("one", one, "e1"));
    Assert(store.Element("two", two, "e1") == "two", "Unrelated window invalidated.");
    time += TimeSpan.FromSeconds(11);
    ExpectError("stale_observation", () => store.Element("two", two, "e1"));
});
Check("wire roundtrip preserves Unicode, empty text and explicit background mode", () =>
{
    var request = new WorkerRequest("id", "perform_action", Wire.Element(new ActionRequest("window", "set_value", "background_only", "obs", "e1", "订单编号😀")));
    var copy = JsonSerializer.Deserialize<WorkerRequest>(JsonSerializer.Serialize(request, Wire.Json), Wire.Json)!;
    var action = copy.Arguments.Deserialize<ActionRequest>(Wire.Json)!;
    Assert(action.Text == "订单编号😀" && action.Mode == "background_only", "Wire serialization changed the request.");
});
Check("SendInput structure matches Windows pointer-sized ABI", () =>
{
    Assert(Marshal.SizeOf<Native.Input>() == (IntPtr.Size == 8 ? 40 : 28), "INPUT size does not match the Windows ABI.");
    Assert(Marshal.OffsetOf<Native.Input>(nameof(Native.Input.Data)).ToInt32() == (IntPtr.Size == 8 ? 8 : 4), "INPUT union alignment is incorrect.");
});
Check("Unicode and extended-key packets distinguish scan codes and virtual keys", () =>
{
    var unicode = Native.Key('订', unicode: true).Data.Keyboard;
    Assert(unicode.Key == 0 && unicode.Scan == '订' && unicode.Flags == 4, "Incorrect Unicode packet.");
    var release = Native.Key(0x25, up: true).Data.Keyboard;
    Assert(release.Key == 0x25 && release.Scan == 0 && release.Flags == 3, "Arrow release must carry KEYUP and EXTENDEDKEY.");
    Assert(Native.Key(0x5B).Data.Keyboard.Flags == 1, "Windows key must be extended.");
});
Check("tool results preserve structured errors and actual image bytes", () =>
{
    var error = DesktopTools.Result(new("id", Error: new("stale_window", "Gone")));
    Assert(error.IsError == true && error.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString() == "stale_window", "Missing structured error.");
    byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
    var result = DesktopTools.Result(new("id", Wire.Element(new { imageWidth = 1 }), ImageBase64: Convert.ToBase64String(png)));
    var json = JsonSerializer.SerializeToElement(result);
    Assert(json.GetProperty("content")[1].GetProperty("data").GetString() == Convert.ToBase64String(png), "Image is not an MCP base64 image.");
});

string executable = Environment.ProcessPath!;
string[] workerArgs = Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
    ? [Assembly.GetExecutingAssembly().Location, "--fake-worker"] : ["--fake-worker"];
await CheckAsync("worker transport restarts after timeout and never calls an uncertain mutation failed", async () =>
{
    using var client = new WorkerClient(new(executable, workerArgs, RequireWindows: false));
    var first = await client.CallAsync("echo", new { }, 5000, default);
    Assert(first.Error is null, first.Error?.Message ?? "First call failed.");
    int pid = first.Data!.Value.GetProperty("processId").GetInt32();
    var timedOut = await client.CallAsync("perform_action", new { }, 200, default);
    Assert(timedOut.Error?.Execution == "unknown", "Timed-out mutation must be unknown.");
    var recovered = await client.CallAsync("echo", new { }, 5000, default);
    Assert(recovered.Error is null && recovered.Data!.Value.GetProperty("processId").GetInt32() != pid, "Worker was not restarted.");
});
await CheckAsync("stop interrupts a blocked worker without waiting on its queue", async () =>
{
    using var client = new WorkerClient(new(executable, workerArgs, RequireWindows: false));
    await client.CallAsync("echo", new { }, 5000, default);
    var blocked = client.CallAsync("hang", new { }, 10000, default);
    await Task.Delay(100);
    client.Stop();
    var result = await blocked.WaitAsync(TimeSpan.FromSeconds(3));
    Assert(result.Error is not null, "Stopped call reported success.");
});

Console.WriteLine($"{passed} passed; {failed} failed. Native Windows behavior is not covered by these tests.");
Environment.ExitCode = failed == 0 ? 0 : 1;
