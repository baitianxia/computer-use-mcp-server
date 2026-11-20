using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using WindowsComputerUse.Core;
using WindowsComputerUse.Worker;

internal sealed partial class McpProbe
{
    public static async Task<int> RunMatrix(string[] args)
    {
        string Option(string name, string? fallback = null)
        {
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback ?? throw new ArgumentException($"Missing {name}.");
        }
        string reports = Path.GetFullPath(Option("--report-dir", "artifacts/acceptance"));
        Directory.CreateDirectory(reports);
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("Native matrix requires Windows; not run."); return 2; }
        Native.SetProcessDpiAwarenessContext((nint)(-4));
        var groups = new List<object>();
        int failed = 0;
        Process? fixture = null, guard = null;
        using var probe = new McpProbe(Option("--server"), reports);
        async Task Group(string name, Func<Task> action)
        {
            try { await action(); groups.Add(new { name, status = "passed", error = (string?)null }); }
            catch (Exception ex) { failed++; groups.Add(new { name, status = "failed", error = ex.Message }); Console.Error.WriteLine($"FAIL group {name}: {ex.Message}"); }
        }
        try
        {
            await probe.Request("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "windows-cu-matrix", version = "0.2.0" } });
            await probe.server.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            fixture = Process.Start(new ProcessStartInfo(Option("--wpf")) { UseShellExecute = false }) ?? throw new Exception("Cannot launch WPF fixture.");
            var window = await probe.FindWindow("CU WPF Controls", fixture.Id);
            string wr = window.GetProperty("windowRef").GetString()!;
            nint hwnd = (nint)window.GetProperty("identity").GetProperty("handle").GetInt64();
            async Task<JsonElement> Inspect(ElementSelector? selector = null, int depth = 12, int nodes = 500) =>
                Payload(await probe.Tool("inspect_window", new { request = new InspectRequest(wr, depth, nodes, selector) }));
            static JsonElement Element(JsonElement obs, string aid) => obs.GetProperty("elements").EnumerateArray().Single(e => e.GetProperty("automationId").GetString() == aid);
            async Task<JsonElement> Act(string aid, string action, string? text = null, double? number = null, string vertical = "no_amount", bool error = false)
            {
                var obs = await Inspect(new(AutomationId: aid));
                var e = Element(obs, aid);
                return Payload(await probe.Tool("perform_action", new { request = new ActionRequest(wr, action, "background_only",
                    obs.GetProperty("observationId").GetString(), e.GetProperty("elementRef").GetString(), Text: text, Number: number, Vertical: vertical) }, error));
            }
            async Task<JsonElement> Wait(string aid, string condition, string? text = null, int depth = 12, int nodes = 500) =>
                Payload(await probe.Tool("wait_for", new { request = new WaitRequest(wr, text, 1500, true, condition, new(AutomationId: aid), depth, nodes) }));
            async Task State(string aid, string condition)
            {
                var result = await Wait(aid, condition);
                probe.Record($"{aid}: {condition}", result.GetProperty("predicateMatched").GetBoolean(), "fresh UIA predicate through MCP");
            }

            await Group("Selectors and wait conditions", async () =>
            {
                var listed = Payload(await probe.Tool("list_windows", new { selector = new WindowSelector(ProcessName: "CuWpfDemo", TitleContains: "WPF", ProcessId: fixture.Id) }));
                probe.Record("Window selector combines process, PID and title", listed.GetArrayLength() == 1 && listed[0].GetProperty("dpi").GetUInt32() > 0
                    && !string.IsNullOrEmpty(listed[0].GetProperty("className").GetString()), listed.GetRawText());
                var selected = await Inspect(new(AutomationId: "EditableText", ControlType: "Edit"));
                probe.Record("Filtered inspection returns one observed control", selected.GetProperty("filtered").GetBoolean()
                    && selected.GetProperty("elements").GetArrayLength() == 1 && selected.GetProperty("scannedNodes").GetInt32() > 1, "matched AutomationId AND control type");
                await Act("EditableText", "set_value", "WPF 中文😀");
                var value = await Wait("EditableText", "value_equals", "WPF 中文😀");
                probe.Record("Value wait verifies exact Unicode data", value.GetProperty("predicateMatched").GetBoolean(), "WPF 中文😀");
                var readOnly = await Act("ReadOnlyText", "set_value", "must not replace", error: true);
                probe.Record("Read-only control refuses mutation", readOnly.GetProperty("error").GetProperty("code").GetString() == "read_only", "UIA read-only guard");
                await State("DoesNotExist", "absent");
                var incomplete = await Wait("DoesNotExist", "absent", depth: 0, nodes: 1);
                probe.Record("Truncated tree cannot prove absence", !incomplete.GetProperty("predicateMatched").GetBoolean()
                    && incomplete.GetProperty("reason").GetString() == "incomplete_observation", "depth=0, maxNodes=1");
                await Act("DelayedButton", "invoke");
                var completed = await Wait("DelayedStatus", "text", "Status: complete");
                probe.Record("Wait follows asynchronous UI update", completed.GetProperty("predicateMatched").GetBoolean(), "fixture timer completed");
                await State("DelayedButton", "enabled");
            });

            await Group("Native UIA patterns", async () =>
            {
                await Act("OptionToggle", "toggle"); await State("OptionToggle", "toggle_on");
                await Act("OptionToggle", "toggle"); await State("OptionToggle", "toggle_off");
                await Act("Choice01", "select"); await State("Choice01", "selected");
                await Act("Choice02", "add_selection"); await State("Choice02", "selected"); await State("Choice01", "selected");
                await Act("Choice01", "remove_selection"); await State("Choice01", "unselected");
                await Act("OrdersFolder", "expand"); await State("OrdersFolder", "expanded"); await State("PendingOrders", "exists");
                await Act("OrdersFolder", "collapse"); await State("OrdersFolder", "collapsed");
                await Act("AmountSlider", "set_range_value", number: 73);
                var slider = Element(await Inspect(new(AutomationId: "AmountSlider")), "AmountSlider").GetProperty("state");
                probe.Record("RangeValue changes and exposes limits", slider.GetProperty("rangeValue").GetDouble() == 73
                    && slider.GetProperty("rangeMinimum").GetDouble() == 0 && slider.GetProperty("rangeMaximum").GetDouble() == 100, slider.GetRawText());
                var rangeError = await Act("AmountSlider", "set_range_value", number: 101, error: true);
                probe.Record("Out of range value is not executed", rangeError.GetProperty("error").GetProperty("code").GetString() == "invalid_argument", "101 outside 0..100");
                await Act("ChoiceList", "scroll_pattern", vertical: "large_increment");
                var scrolled = Element(await Inspect(new(AutomationId: "ChoiceList")), "ChoiceList").GetProperty("state");
                probe.Record("ScrollPattern moves the list", scrolled.GetProperty("verticalScrollPercent").GetDouble() > 0, scrolled.GetRawText());
                await Act("Choice30", "scroll_into_view");
                probe.Record("ScrollItem brings last item into view", !Element(await Inspect(new(AutomationId: "Choice30")), "Choice30").GetProperty("offscreen").GetBoolean(), "Choice30 visible");
            });

            await Group("Background operations and occluded capture", async () =>
            {
                guard = Process.Start(new ProcessStartInfo(Option("--demo")) { UseShellExecute = false }) ?? throw new Exception("Cannot launch foreground fixture.");
                var foregroundWindow = await probe.FindWindow("CU Demo Orders", guard.Id);
                nint guardHwnd = (nint)foregroundWindow.GetProperty("identity").GetProperty("handle").GetInt64();
                Native.GetWindowRect(hwnd, out var rect);
                if (!SetWindowPos(guardHwnd, 0, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, 0x4)) throw new Exception("Cannot cover WPF fixture.");
                await probe.Tool("perform_action", new { request = new ActionRequest(foregroundWindow.GetProperty("windowRef").GetString()!, "activate") });
                int samples = 0, changes = 0; long maxIntervalMs = 0;
                using var monitor = new CancellationTokenSource();
                var sampling = Task.Run(async () =>
                {
                    var watch = Stopwatch.StartNew(); long previous = 0;
                    while (!monitor.IsCancellationRequested)
                    {
                        long current = watch.ElapsedMilliseconds; maxIntervalMs = Math.Max(maxIntervalMs, current - previous); previous = current;
                        Interlocked.Increment(ref samples);
                        if (GetForegroundWindow() != guardHwnd) Interlocked.Increment(ref changes);
                        await Task.Delay(10);
                    }
                });
                try
                {
                    await Act("EditableText", "set_value", "Background verified");
                    var waited = await Wait("EditableText", "value_equals", "Background verified");
                    probe.Record("Background UIA writes exact value", waited.GetProperty("predicateMatched").GetBoolean(), "separate demo owns foreground");
                    var capture = await probe.Tool("capture_window", new { request = new CaptureRequest(wr, "wgc") });
                    var bytes = PngBytes(capture); var point = FindColor(bytes, Color.MediumSeaGreen);
                    await File.WriteAllBytesAsync(Path.Combine(reports, "wpf-occluded.png"), bytes);
                    probe.Record("WGC captures covered WPF window", Payload(capture).GetProperty("method").GetString() == "wgc", $"WPF green surface detected at {point}; foreground cover window overlaps entire target");
                }
                finally { monitor.Cancel(); await sampling; }
                probe.Record("Background focus sampling detects no foreground change", samples > 0 && changes == 0,
                    $"{samples} samples, {changes} mismatches, maximum interval {maxIntervalMs} ms; sampling is not a continuous guarantee");
                guard.CloseMainWindow(); await guard.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); guard.Dispose(); guard = null;
            });

            await Group("Native pointer operations", async () =>
            {
                if (guard is { HasExited: false }) { guard.Kill(); await guard.WaitForExitAsync(); }
                await probe.Tool("perform_action", new { request = new ActionRequest(wr, "activate") });
                async Task Pointer(string action, string button = "left")
                {
                    var capture = await probe.Tool("capture_window", new { request = new CaptureRequest(wr, "wgc") });
                    var point = FindColor(PngBytes(capture), Color.MediumSeaGreen);
                    await probe.Tool("perform_action", new { request = new ActionRequest(wr, action,
                        ObservationId: Payload(capture).GetProperty("observationId").GetString(), X: point.X, Y: point.Y,
                        ToX: action == "drag" ? point.X + 100 : null, ToY: action == "drag" ? point.Y : null, Button: button) });
                }
                async Task Evidence(string key, int minimum)
                {
                    var observed = await Inspect(new(AutomationId: "PointerEvidence"));
                    string evidence = Element(observed, "PointerEvidence").GetProperty("name").GetString()!;
                    var match = Regex.Match(evidence, $@"{key}=(-?\d+)");
                    probe.Record($"SendInput {key}", match.Success && Math.Abs(int.Parse(match.Groups[1].Value)) >= minimum, evidence);
                }
                await Pointer("move"); await Evidence("Move", 1);
                await Pointer("double_click"); await Evidence("Double", 1);
                await Pointer("click", "right"); await Evidence("Right", 1);
                await Pointer("scroll"); await Evidence("Wheel", 120);
                await Pointer("drag"); await Evidence("Drag", 1);
                var captureBefore = Payload(await probe.Tool("capture_window", new { request = new CaptureRequest(wr, "wgc") }));
                Native.GetWindowRect(hwnd, out var before);
                SetWindowPos(hwnd, 0, 0, 0, before.Right - before.Left + 30, before.Bottom - before.Top + 20, 0x16);
                var stale = Payload(await probe.Tool("perform_action", new { request = new ActionRequest(wr, "click", ObservationId: captureBefore.GetProperty("observationId").GetString(), X: 200, Y: 100) }, true));
                probe.Record("Resize invalidates captured coordinates", stale.GetProperty("error").GetProperty("code").GetString() == "stale_capture", "no input sent to resized window");
                var final = await probe.Tool("capture_window", new { request = new CaptureRequest(wr, "wgc") });
                await File.WriteAllBytesAsync(Path.Combine(reports, "wpf-final.png"), PngBytes(final));
            });
        }
        catch (Exception ex) { failed++; groups.Add(new { name = "Matrix setup", status = "failed", error = ex.Message }); Console.Error.WriteLine(ex); }
        finally
        {
            foreach (var process in new[] { guard, fixture })
            {
                try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                process?.Dispose();
            }
            var report = new { timestamp = DateTimeOffset.UtcNow, sourceCommit = Environment.GetEnvironmentVariable("WINDOWS_CU_SOURCE_COMMIT"),
                os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                userInteractive = Environment.UserInteractive, scope = "WPF/UIA/native input/background matrix", passed = failed == 0,
                groups, checks = probe.checks, virtualScreen = new { x = Native.GetSystemMetrics(76), y = Native.GetSystemMetrics(77), width = Native.GetSystemMetrics(78), height = Native.GetSystemMetrics(79), monitors = Native.GetSystemMetrics(80) },
                notRun = new[] { "Claude model-driven tasks", "Real business client", "Physical multi-monitor DPI changes", "Simultaneous human keyboard/mouse use", "Capture during active resize", "HDR display" } };
            await File.WriteAllTextAsync(Path.Combine(reports, "windows-matrix.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions(Wire.Json) { WriteIndented = true }));
        }
        return failed == 0 ? 0 : 1;
    }
}
