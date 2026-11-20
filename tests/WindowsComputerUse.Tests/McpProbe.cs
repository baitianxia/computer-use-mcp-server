using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using WindowsComputerUse.Core;

internal sealed partial class McpProbe : IDisposable
{
    private readonly Process server;
    private readonly List<object> checks = [];
    private int id;
    private McpProbe(string path, string reportDirectory)
    {
        Directory.CreateDirectory(reportDirectory);
        var info = new ProcessStartInfo(path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? "dotnet" : path)
        { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(path);
        info.Environment["WINDOWS_CU_LOG_DIR"] = Path.Combine(reportDirectory, "logs");
        info.Environment["WINDOWS_CU_GUIDANCE_DIR"] = Path.Combine(reportDirectory, "guidance-data");
        info.Environment["CLAUDE_CONFIG_DIR"] = Path.Combine(reportDirectory, "guidance-claude");
        server = Process.Start(info) ?? throw new Exception("Could not start MCP server.");
        server.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine(e.Data); };
        server.BeginErrorReadLine();
    }

    private async Task<JsonElement> Request(string method, object? arguments = null)
    {
        int current = ++id;
        await server.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = current, method, @params = arguments }, Wire.Json));
        await server.StandardInput.FlushAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (true)
        {
            string? line = await server.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null) throw new Exception("MCP server closed stdout.");
            var response = JsonDocument.Parse(line).RootElement.Clone();
            if (!response.TryGetProperty("id", out var responseId)) continue;
            if (responseId.GetInt32() != current) throw new Exception("Wrong MCP response id.");
            if (response.TryGetProperty("error", out var error)) throw new Exception(error.GetRawText());
            return response.GetProperty("result");
        }
    }

    private async Task<JsonElement> Tool(string name, object arguments, bool expectError = false)
    {
        var result = await Request("tools/call", new { name, arguments });
        bool isError = result.TryGetProperty("isError", out var error) && error.GetBoolean();
        if (isError != expectError) throw new Exception($"Unexpected tool outcome for {name}: {result.GetRawText()}");
        return result;
    }

    private static JsonElement Payload(JsonElement result)
    {
        var payload = result.TryGetProperty("structuredContent", out var structured) ? structured
            : JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.Clone();
        return payload.TryGetProperty("data", out var data) ? data : payload;
    }

    private void Record(string name, bool passed, string evidence)
    {
        checks.Add(new { name, passed, evidence });
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}: {evidence}");
        if (!passed) throw new Exception(name + ": " + evidence);
    }

    public static async Task<int> Run(string[] args, bool desktop)
    {
        string Option(string name, string? fallback = null)
        {
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback ?? throw new ArgumentException($"Missing {name}.");
        }
        string reportDirectory = Path.GetFullPath(Option("--report-dir", "artifacts/acceptance"));
        if (desktop && !OperatingSystem.IsWindows()) { Console.Error.WriteLine("Windows desktop smoke test requires an interactive Windows session; not run."); return 2; }
        using var probe = new McpProbe(Option("--server"), reportDirectory);
        Process? demo = null;
        bool allPassed = false;
        string? failure = null;
        try
        {
            var initialize = await probe.Request("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "windows-cu-probe", version = "0.1.0" } });
            probe.Record("MCP initialize", initialize.TryGetProperty("serverInfo", out _), initialize.GetProperty("protocolVersion").GetString()!);
            await probe.server.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            var listed = await probe.Request("tools/list");
            var tools = listed.GetProperty("tools").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!, t => t.Clone());
            foreach (var name in new[] { "list_windows", "inspect_window", "capture_window", "perform_action", "wait_for", "stop", "guide_task", "read_guidance", "save_client_skill" })
                probe.Record("Tool schema " + name, tools.ContainsKey(name) && tools[name].GetProperty("inputSchema").GetProperty("type").GetString() == "object", "discovered through MCP tools/list");
            var rejected = Payload(await probe.Tool("perform_action", new { request = new ActionRequest("missing", "click", "background_only") }, true));
            probe.Record("Background request never silently becomes input", rejected.GetProperty("error").GetProperty("code").GetString() == "foreground_required", "structured error from MCP tool call");
            var invalidCapture = Payload(await probe.Tool("capture_window", new { request = new CaptureRequest("missing", "unknown") }, true));
            probe.Record("Invalid capture backend is rejected before desktop access", invalidCapture.GetProperty("error").GetProperty("code").GetString() == "invalid_argument", "structured capture validation from MCP tool call");
            if (!desktop)
            {
                if (!OperatingSystem.IsWindows())
                {
                    var platform = Payload(await probe.Tool("list_windows", new { }, true));
                    probe.Record("Unsupported platform is explicit", platform.GetProperty("error").GetProperty("code").GetString() == "unsupported_platform", "Windows operations were not simulated");
                }
                allPassed = true;
                return 0;
            }
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            WindowsComputerUse.Worker.Native.SetProcessDpiAwarenessContext((nint)(-4));
            demo = Process.Start(new ProcessStartInfo(Option("--demo")) { UseShellExecute = false }) ?? throw new Exception("Cannot launch demo.");
            JsonElement selected = default;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                var windows = Payload(await probe.Tool("list_windows", new { query = "CU Demo Orders" }));
                selected = windows.EnumerateArray().FirstOrDefault(w => w.GetProperty("identity").GetProperty("processId").GetInt32() == demo.Id);
                if (selected.ValueKind != JsonValueKind.Undefined) break;
                await Task.Delay(250);
            }
            probe.Record("Find launched demo by title and PID", selected.ValueKind == JsonValueKind.Object, $"demo PID {demo.Id}");
            string windowRef = selected.GetProperty("windowRef").GetString()!;
            var observation = Payload(await probe.Tool("inspect_window", new { request = new InspectRequest(windowRef) }));
            async Task<JsonElement> Action(JsonElement observed, string action, string elementRef, string? text = null)
            {
                var result = Payload(await probe.Tool("perform_action", new { request = new ActionRequest(windowRef, action, "background_only", observed.GetProperty("observationId").GetString(), elementRef, text) }));
                probe.Record("Action does not claim business success", result.GetProperty("execution").GetString() == "executed" && result.GetProperty("verification").GetString() == "not_verified", action);
                return result.GetProperty("observation");
            }
            string Find(JsonElement observed, string name) => observed.GetProperty("elements").EnumerateArray().First(e => e.GetProperty("name").GetString() == name).GetProperty("elementRef").GetString()!;
            for (int queryNumber = 1; queryNumber <= 2; queryNumber++)
            {
                string order = queryNumber == 1 ? "A1001" : "A1002";
                nint foregroundBefore = GetForegroundWindow();
                observation = await Action(observation, "set_value", Find(observation, "Order number"), order);
                observation = await Action(observation, "invoke", Find(observation, "Query"));
                var waited = Payload(await probe.Tool("wait_for", new { request = new WaitRequest(windowRef, $"Completed query #{queryNumber}", 5000, true) }));
                probe.Record("Query completed with new sequence", waited.GetProperty("predicateMatched").GetBoolean(), $"query #{queryNumber}");
                observation = waited.GetProperty("observation");
                string expected = queryNumber == 1 ? "Order A1001 | Ready | Amount 128.00 | Query #1" : "Order A1002 | Shipped | Amount 256.50 | Query #2";
                probe.Record("Exact business result", observation.GetProperty("elements").EnumerateArray().Any(e => e.GetProperty("name").GetString() == expected), expected);
                probe.Record("Foreground unchanged at check points", foregroundBefore == GetForegroundWindow(), "before/after only; continuous focus/noninterference still requires manual verification");
            }
            var screenshot = await probe.Tool("capture_window", new { request = new CaptureRequest(windowRef, "wgc") });
            var captureMetadata = Payload(screenshot);
            probe.Record("Explicit WGC capture uses WGC without fallback", captureMetadata.GetProperty("method").GetString() == "wgc"
                && captureMetadata.GetProperty("requestedMethod").GetString() == "wgc"
                && captureMetadata.GetProperty("frameSystemRelativeTimeTicks").GetInt64() > 0,
                "Windows Graphics Capture frame timestamp and actual backend returned");
            probe.Record("WGC capture includes coordinate geometry", captureMetadata.GetProperty("windowBounds").ValueKind == JsonValueKind.Object
                && captureMetadata.GetProperty("boundsSource").GetString() is "dwm_extended_frame_bounds" or "window_rect",
                captureMetadata.GetProperty("boundsSource").GetString()!);
            var image = screenshot.GetProperty("content").EnumerateArray().First(c => c.GetProperty("type").GetString() == "image");
            byte[] bytes = Convert.FromBase64String(image.GetProperty("data").GetString()!);
            probe.Record("PNG image returned through MCP", bytes.Length > 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), $"{bytes.Length} bytes; visual correctness requires review");
            await File.WriteAllBytesAsync(Path.Combine(reportDirectory, "demo-capture.png"), bytes);

            // Locate a known painted fixture in the actual returned pixels. UIA does not expose
            // the colored regions as buttons, so this also tests WGC-to-SendInput coordinates.
            var orange = FindColor(bytes, Color.DarkOrange);
            var clicked = Payload(await probe.Tool("perform_action", new { request = new ActionRequest(windowRef, "click",
                ObservationId: captureMetadata.GetProperty("observationId").GetString(), X: orange.X, Y: orange.Y) }));
            observation = clicked.GetProperty("observation");
            probe.Record("Screenshot coordinates click the painted Orange fixture",
                HasText(observation, "Canvas selection: Orange"), $"PNG pixel ({orange.X},{orange.Y}); native SendInput mouse path");
            var staleCapture = Payload(await probe.Tool("perform_action", new { request = new ActionRequest(windowRef, "click",
                ObservationId: captureMetadata.GetProperty("observationId").GetString(), X: orange.X, Y: orange.Y) }, true));
            probe.Record("An action invalidates its screenshot reference", staleCapture.GetProperty("error").GetProperty("code").GetString() == "stale_observation",
                "the same screenshot cannot be reused for another click");

            observation = Payload(await probe.Tool("inspect_window", new { request = new InspectRequest(windowRef) }));
            async Task<JsonElement> Input(string action, string? text = null, string[]? keys = null)
            {
                var performed = Payload(await probe.Tool("perform_action", new { request = new ActionRequest(windowRef, action,
                    ObservationId: observation.GetProperty("observationId").GetString(), ElementRef: Find(observation, "Order number"), Text: text, Keys: keys) }));
                return performed.GetProperty("observation");
            }
            observation = await Input("press_keys", keys: ["CTRL", "A"]);
            const string unicodeOrder = "测试订单😀";
            observation = await Input("type_text", text: unicodeOrder);
            probe.Record("Unicode SendInput preserves Chinese text and surrogate pairs",
                ValueOf(observation, "Order number") == unicodeOrder, unicodeOrder);
            observation = await Input("press_keys", keys: ["CTRL", "A"]);
            observation = await Input("press_keys", keys: ["BACKSPACE"]);
            probe.Record("Keyboard shortcuts can select and clear the input", ValueOf(observation, "Order number") == "", "CTRL+A then BACKSPACE");
            observation = await Action(observation, "set_value", Find(observation, "Order number"), "A1002");

            await probe.Tool("perform_action", new { request = new ActionRequest(windowRef, "click",
                ObservationId: observation.GetProperty("observationId").GetString(), ElementRef: Find(observation, "Show dialog")) });
            var dialog = await probe.FindWindow("CU Demo Dialog", demo.Id);
            string dialogRef = dialog.GetProperty("windowRef").GetString()!;
            probe.Record("A modal dialog is discovered as an owned window",
                dialog.GetProperty("ownerHandle").GetInt64() == selected.GetProperty("identity").GetProperty("handle").GetInt64(), "dialog found after native click");
            var dialogObservation = Payload(await probe.Tool("inspect_window", new { request = new InspectRequest(dialogRef) }));
            probe.Record("Modal dialog contents can be inspected", HasText(dialogObservation, "This is a dynamic dialog. Close it and inspect the main window again."), "dialog UIA text verified");
            await probe.Tool("perform_action", new { request = new ActionRequest(dialogRef, "press_keys", Keys: ["ALT", "F4"]) });
            var remainingDialogs = Payload(await probe.Tool("list_windows", new { query = "CU Demo Dialog" }));
            probe.Record("Keyboard shortcut closes the dialog and main window remains readable",
                !remainingDialogs.EnumerateArray().Any(w => w.GetProperty("identity").GetProperty("processId").GetInt32() == demo.Id)
                && HasText(Payload(await probe.Tool("inspect_window", new { request = new InspectRequest(windowRef) })), "Completed query #2"), "ALT+F4, re-list and inspect");

            var beforeMove = Payload(await probe.Tool("capture_window", new { request = new CaptureRequest(windowRef, "wgc") }));
            nint hwnd = (nint)selected.GetProperty("identity").GetProperty("handle").GetInt64();
            var rectangle = beforeMove.GetProperty("windowBounds");
            if (!SetWindowPos(hwnd, 0, rectangle.GetProperty("x").GetInt32() + 40, rectangle.GetProperty("y").GetInt32() + 25, 0, 0, 0x15))
                throw new Exception($"Could not move the test window: {Marshal.GetLastWin32Error()}.");
            var moved = Payload(await probe.Tool("perform_action", new { request = new ActionRequest(windowRef, "click",
                ObservationId: beforeMove.GetProperty("observationId").GetString(), X: orange.X, Y: orange.Y) }, true));
            probe.Record("Window movement rejects old screenshot coordinates before input",
                moved.GetProperty("error").GetProperty("code").GetString() == "stale_capture"
                && moved.GetProperty("error").GetProperty("execution").GetString() == "not_executed", "fixture moved by Win32; old capture rejected");
            var afterMove = await probe.Tool("capture_window", new { request = new CaptureRequest(windowRef, "wgc", 640) });
            var afterMoveMetadata = Payload(afterMove);
            byte[] movedImage = PngBytes(afterMove);
            var blue = FindColor(movedImage, Color.RoyalBlue);
            var blueClick = Payload(await probe.Tool("perform_action", new { request = new ActionRequest(windowRef, "click",
                ObservationId: afterMoveMetadata.GetProperty("observationId").GetString(), X: blue.X, Y: blue.Y) }));
            probe.Record("New scaled WGC image maps to the moved window",
                afterMoveMetadata.GetProperty("imageWidth").GetInt32() == 640 && HasText(blueClick.GetProperty("observation"), "Canvas selection: Blue"),
                $"640-pixel-wide PNG, detected Blue at ({blue.X},{blue.Y})");
            await File.WriteAllBytesAsync(Path.Combine(reportDirectory, "demo-moved-scaled.png"), movedImage);
            ShowWindow(hwnd, 6);
            var minimized = Payload(await probe.Tool("capture_window", new { request = new CaptureRequest(windowRef, "wgc") }, true));
            probe.Record("Minimized windows cannot return an apparently fresh capture",
                minimized.GetProperty("error").GetProperty("code").GetString() == "window_minimized", "explicit rejection");
            ShowWindow(hwnd, 9);
            await probe.Tool("capture_window", new { request = new CaptureRequest(windowRef, "wgc") });
            probe.Record("Restored windows can be captured again", true, "strict WGC capture succeeded after restore");

            await probe.Tool("stop", new { });
            var stale = Payload(await probe.Tool("inspect_window", new { request = new InspectRequest(windowRef) }, true));
            probe.Record("Restart invalidates old window references", stale.GetProperty("error").GetProperty("code").GetString() == "stale_window", "old reference rejected after worker stop");
            var rediscovered = await probe.FindWindow("CU Demo Orders", demo.Id);
            string rediscoveredRef = rediscovered.GetProperty("windowRef").GetString()!;
            probe.Record("Window can be rediscovered after worker restart", rediscoveredRef != windowRef, "new worker reference returned for the same demo process");
            if (!demo.CloseMainWindow()) throw new Exception("Could not close the owned demo window.");
            await demo.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var closed = Payload(await probe.Tool("inspect_window", new { request = new InspectRequest(rediscoveredRef) }, true));
            probe.Record("Closing the application invalidates its window reference", closed.GetProperty("error").GetProperty("code").GetString() == "stale_window", "closed process reference rejected");
            demo.Dispose();
            demo = Process.Start(new ProcessStartInfo(Option("--demo")) { UseShellExecute = false }) ?? throw new Exception("Cannot reopen demo.");
            var reopened = await probe.FindWindow("CU Demo Orders", demo.Id);
            var reopenedObservation = Payload(await probe.Tool("inspect_window", new { request = new InspectRequest(reopened.GetProperty("windowRef").GetString()!) }));
            probe.Record("Reopened application is found and inspected with a new reference",
                reopened.GetProperty("windowRef").GetString() != rediscoveredRef && HasText(reopenedObservation, "Idle - no query completed"), "fresh process instance and initial state");
            allPassed = true;
            return 0;
        }
        catch (Exception ex) { failure = ex.Message; Console.Error.WriteLine(failure); return 1; }
        finally
        {
            if (demo is { HasExited: false }) demo.Kill();
            demo?.Dispose();
            var report = new { timestamp = DateTimeOffset.UtcNow, os = RuntimeInformation.OSDescription,
                osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                userInteractive = Environment.UserInteractive,
                sessionId = OperatingSystem.IsWindows() ? Process.GetCurrentProcess().SessionId : (int?)null,
                sourceCommit = Environment.GetEnvironmentVariable("WINDOWS_CU_SOURCE_COMMIT"),
                scope = desktop ? "MCP and native demo smoke test" : "MCP protocol only", passed = allPassed, failure,
                checks = probe.checks, claudeCodeIntegrationVerified = false,
                remaining = new[] { "Claude Code /mcp and Skill discovery", "Claude-driven visual fallback (fixture pixels are detected deterministically)", "Windows 10/11, occlusion, multi-monitor DPI and in-flight resize validation", "Continuous foreground noninterference", "Real business client acceptance" } };
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, desktop ? "windows-smoke.json" : "protocol-smoke.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions(Wire.Json) { WriteIndented = true }));
        }
    }

    private async Task<JsonElement> FindWindow(string title, int processId)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            var found = Payload(await Tool("list_windows", new { query = title })).EnumerateArray()
                .FirstOrDefault(w => w.GetProperty("identity").GetProperty("processId").GetInt32() == processId);
            if (found.ValueKind == JsonValueKind.Object) return found;
            await Task.Delay(150);
        }
        throw new Exception($"Window '{title}' was not found for process {processId}.");
    }

    private static bool HasText(JsonElement observed, string text) => observed.GetProperty("elements").EnumerateArray().Any(e => e.GetProperty("name").GetString() == text);
    private static string? ValueOf(JsonElement observed, string name) => observed.GetProperty("elements").EnumerateArray().First(e => e.GetProperty("name").GetString() == name).GetProperty("value").GetString();
    private static byte[] PngBytes(JsonElement result) => Convert.FromBase64String(result.GetProperty("content").EnumerateArray().First(c => c.GetProperty("type").GetString() == "image").GetProperty("data").GetString()!);

    [SupportedOSPlatform("windows")]
    private static (int X, int Y) FindColor(byte[] png, Color target)
    {
        using var stream = new MemoryStream(png);
        using var bitmap = new Bitmap(stream);
        int left = bitmap.Width, top = bitmap.Height, right = -1, bottom = -1, count = 0;
        for (int y = 0; y < bitmap.Height; y += 2)
            for (int x = 0; x < bitmap.Width; x += 2)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (Math.Abs(pixel.R - target.R) > 3 || Math.Abs(pixel.G - target.G) > 3 || Math.Abs(pixel.B - target.B) > 3) continue;
                left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); count++;
            }
        if (count < 100) throw new Exception($"Expected painted fixture {target.Name} was not visible in the captured PNG.");
        return ((left + right) / 2, (top + bottom) / 2);
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    public void Dispose() { try { if (!server.HasExited) server.Kill(entireProcessTree: true); } finally { server.Dispose(); } }
}
