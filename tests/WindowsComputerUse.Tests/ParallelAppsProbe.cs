using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using WindowsComputerUse.Core;

internal sealed partial class McpProbe
{
    private sealed record AppCheck(string Name, string Status, string? Error);
    private sealed record InputWindow(string Action, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc);

    internal static async Task<JsonElement> CaptureStableAppFrame(CaptureRequest request, Func<CaptureRequest, Task<JsonElement>> capture)
    {
        var clock = Stopwatch.StartNew();
        try { return await capture(request); }
        catch (DesktopException ex) when (ex.Error is { Retryable: true, Execution: "not_executed", Code: "stale_capture" or "capture_geometry_mismatch" })
        {
            // A newly opened app may still be resizing. Make at most one fresh capture with the same backend.
            await Task.Delay(200);
            if (clock.Elapsed >= TimeSpan.FromSeconds(6)) throw;
            Console.WriteLine($"Retrying {request.Method} capture after {ex.Error.Code}: {ex.Message}");
            return await capture(request);
        }
    }

    public static async Task<int> RunParallelApps(string[] args)
    {
        string Option(string name)
        {
            int index = Array.IndexOf(args, name);
            if (index < 0 || index + 1 >= args.Length) throw new ArgumentException($"Missing {name}.");
            return args[index + 1];
        }
        string folder = Path.GetFullPath(Option("--report-dir"));
        string runId = Guid.ParseExact(Option("--run-id"), "N").ToString("N");
        List<AppCheck> appChecks = [];
        List<InputWindow> inputWindows = [];
        string? failure = null;
        string status = "blocked";
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Requires Windows.");
            var binding = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "parent-session.json"))).RootElement;
            int sessionId = Process.GetCurrentProcess().SessionId;
            using var parent = Process.GetProcessById(binding.GetProperty("parentProcessId").GetInt32());
            if (sessionId <= 0 || binding.GetProperty("runId").GetString() != runId ||
                binding.GetProperty("machineName").GetString() != Environment.MachineName ||
                sessionId != binding.GetProperty("childSessionId").GetInt32() || sessionId == parent.SessionId ||
                parent.SessionId != binding.GetProperty("parentSessionId").GetInt32() ||
                !File.Exists(Path.Combine(folder, "parent-start.json")))
                throw new InvalidOperationException("Run this probe inside the assigned child session after the parent starts it.");
            using var probe = new McpProbe(Option("--server"), folder);
            await probe.Request("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "parallel-app-test", version = "1" } });
            await probe.server.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await probe.server.StandardInput.FlushAsync();
            await probe.ProbeChildApps(folder, runId, appChecks, inputWindows);
            if (appChecks.Any(c => c.Status == "failed")) status = "failed";
            else if (appChecks.Count == 3 && appChecks.All(c => c.Status == "passed")) status = "passed";
            failure = string.Join(" / ", appChecks.Where(c => c.Error != null).Select(c => $"{c.Name}: {c.Error}"));
        }
        catch (Exception ex) { failure = ex.Message; }
        foreach (string name in new[] { "Notepad", "Paint", "Edge" })
            if (!appChecks.Any(c => c.Name == name)) appChecks.Add(new(name, "not_run", failure));
        var report = new { runId, status, passed = status == "passed", failure, checks = appChecks, inputWindows };
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "parallel-apps.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions(Wire.Json) { WriteIndented = true }));
        Console.WriteLine($"{status}: {failure}");
        return status switch { "passed" => 0, "failed" => 1, _ => 2 };
    }

    [SupportedOSPlatform("windows")]
    private async Task ProbeChildApps(string folder, string runId, List<AppCheck> results, List<InputWindow> inputWindows)
    {
        string token = "CU-" + runId;
        static string InboxApp(string name)
        {
            var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", name);
            return File.Exists(alias) ? alias : Path.Combine(Environment.SystemDirectory, name);
        }
        async Task DoInput(ActionRequest request)
        {
            DateTimeOffset started = DateTimeOffset.UtcNow;
            var outcome = Payload(await Tool("perform_action", new { request }));
            if (outcome.GetProperty("execution").GetString() != "executed") throw new InvalidOperationException("Input was not executed.");
            inputWindows.Add(new(request.Action, started, DateTimeOffset.UtcNow));
        }

        async Task Scenario(string name, Func<Task> test)
        {
            try { await test(); results.Add(new(name, "passed", null)); }
            catch (Exception ex) when (ex is FileNotFoundException or Win32Exception or TimeoutException)
            { results.Add(new(name, "blocked", ex.Message)); }
            catch (Exception ex) { results.Add(new(name, "failed", ex.Message)); }
        }

        // Match the per-run document title, the application name, and a process created
        // by this run. Packaged Notepad/Paint can hand off from a short-lived launcher.
        async Task<WindowInfo> Launch(string exe, string processName, params string[] arguments)
        {
            if (!File.Exists(exe)) throw new FileNotFoundException("Application executable not found.", exe);
            var start = DateTime.UtcNow;
            var info = new ProcessStartInfo(exe) { UseShellExecute = false };
            foreach (var arg in arguments) info.ArgumentList.Add(arg);
            using var launcher = Process.Start(info) ?? throw new Win32Exception($"Could not start {exe}.");
            var clock = Stopwatch.StartNew();
            do
            {
                var selector = new WindowSelector(ProcessName: processName, TitleContains: token);
                var windows = Payload(await Tool("list_windows", new { selector })).Deserialize<WindowInfo[]>(Wire.Json)!;
                var matches = windows.Where(w => w.Identity.ProcessStartedUtcTicks >= start.Ticks).ToArray();
                if (matches.Length == 1) return matches[0];
                if (matches.Length > 1) throw new InvalidOperationException("Ambiguous per-run document window.");
                await Task.Delay(250);
            } while (clock.Elapsed < TimeSpan.FromSeconds(15));
            throw new TimeoutException($"Could not find a new {processName} process with the test document. Close existing {processName} windows in the child session and retry; check for startup prompts.");
        }

        async Task CheckOutcome(Func<bool> predicate, string message)
        {
            for (int i = 0; i < 24; i++)
            {
                try { if (predicate()) return; } catch (IOException) { } // The app can still be writing.
                await Task.Delay(200);
            }
            throw new InvalidOperationException(message);
        }

        // These are foreground windows on the dedicated child desktop. Some RDP
        // displays render a blank window capture while a screen capture works.
        async Task<(byte[] Png, string ObservationId, int X, int Y)> CaptureTarget(WindowInfo window, Color color)
        {
            var errors = new List<string>();
            foreach (string method in new[] { "auto", "screen" })
            {
                JsonElement shot;
                try
                {
                    shot = await CaptureStableAppFrame(new CaptureRequest(window.WindowRef, method),
                        request => Tool("capture_window", new { request }));
                }
                catch (DesktopException ex) when (method == "auto" && ex.Error.Code == "capture_failed")
                {
                    errors.Add(ex.Message);
                    continue;
                }
                var png = PngBytes(shot);
                try
                {
                    var point = FindColor(png, color);
                    var metadata = Payload(shot);
                    Console.WriteLine($"{window.ProcessName} target found using {metadata.GetProperty("method").GetString()} at {point}.");
                    return (png, metadata.GetProperty("observationId").GetString()!, point.X, point.Y);
                }
                catch (Exception ex) { errors.Add($"{method}: {ex.Message}"); }
                await Task.Delay(300);
            }
            throw new InvalidOperationException("Test target not visible. " + string.Join(" / ", errors));
        }

        // Only close the identified document window, after successfully saving its
        // result. Leave failed windows for inspection, including dialogs and unsaved files.
        async Task CloseTestWindow(WindowInfo window, bool closeTab = false)
        {
            var matches = Payload(await Tool("list_windows", new { selector = new WindowSelector(ProcessId: window.Identity.ProcessId, TitleContains: token) }));
            if (matches.Deserialize<WindowInfo[]>(Wire.Json)!.Any(w => w.Identity == window.Identity))
                await Tool("perform_action", new { request = new ActionRequest(window.WindowRef, "press_keys", Keys: closeTab ? ["CTRL", "W"] : ["ALT", "F4"]) });
        }

        await Scenario("Notepad", async () =>
        {
            string file = Path.Combine(folder, token + ".txt");
            File.WriteAllText(file, "", Encoding.UTF8);
            var window = await Launch(InboxApp("notepad.exe"), "notepad", file);
            string expected = token + " Windows 并行";
            foreach (string part in new[] { token[..11], token[11..], " Windows", " 并行" })
                await DoInput(new(window.WindowRef, "type_text", Text: part));
            await DoInput(new(window.WindowRef, "press_keys", Keys: ["CTRL", "S"]));
            await CheckOutcome(() => File.ReadAllText(file).TrimEnd('\r', '\n') == expected, "Saved Notepad file did not contain the exact per-run text.");
            await CloseTestWindow(window, closeTab: true);
        });
        await Scenario("Paint", async () =>
        {
            string file = Path.Combine(folder, token + ".png");
            Color paper = Color.FromArgb(116, 209, 179);
            using (var bmp = new Bitmap(320, 200))
            {
                using (var graphics = Graphics.FromImage(bmp)) graphics.Clear(paper);
                bmp.Save(file, ImageFormat.Png);
            }
            var window = await Launch(InboxApp("mspaint.exe"), "mspaint", file);
            await Tool("perform_action", new { request = new ActionRequest(window.WindowRef, "activate") });
            var (png, observationId, x, y) = await CaptureTarget(window, paper);
            // Choose both drag endpoints inside one continuous row of the test canvas.
            using var stream = new MemoryStream(png);
            using var image = new Bitmap(stream);
            bool OnPaper(int px) => px >= 0 && px < image.Width && Math.Abs(image.GetPixel(px,y).R - paper.R) <= 3 &&
                Math.Abs(image.GetPixel(px,y).G - paper.G) <= 3 && Math.Abs(image.GetPixel(px,y).B - paper.B) <= 3;
            int dx = 0;
            while (dx < 55 && OnPaper(x - dx) && OnPaper(x + dx)) dx++;
            if (dx < 12) throw new InvalidOperationException("Insufficient visible Paint canvas for a verified drag.");
            await DoInput(new(window.WindowRef, "drag", ObservationId: observationId, X: x-dx+1, Y: y, ToX: x+dx-1, ToY: y));
            await DoInput(new(window.WindowRef, "press_keys", Keys: ["CTRL", "S"]));
            await CheckOutcome(() =>
            {
                // Read a snapshot; Paint may still be replacing the file.
                using var bytes = new MemoryStream(File.ReadAllBytes(file));
                try
                {
                    using var saved = new Bitmap(bytes);
                    if (saved.Width != 320 || saved.Height != 200) return false;
                    int minX = saved.Width, maxX = -1, minY = saved.Height, maxY = -1, longest = 0;
                    for (int row = 0; row < saved.Height; row++)
                    {
                        int stroke = 0;
                        for (int col = 0; col < saved.Width; col++)
                        {
                            if (saved.GetPixel(col, row).ToArgb() == paper.ToArgb()) { stroke = 0; continue; }
                            longest = Math.Max(longest, ++stroke);
                            minX = Math.Min(minX, col); maxX = Math.Max(maxX, col);
                            minY = Math.Min(minY, row); maxY = Math.Max(maxY, row);
                        }
                    }
                    return longest >= 10 && maxX - minX >= 3 * (maxY - minY + 1)
                        && minX > 0 && maxX < saved.Width - 1 && minY > 0 && maxY < saved.Height - 1;
                }
                catch (ArgumentException) { return false; } // An incomplete PNG is not evidence of a stroke.
            }, "Saved Paint PNG does not contain a horizontal stroke. Check the active Paint tool.");
            await CloseTestWindow(window);
        });
        await Scenario("Edge", async () =>
        {
            string? edge = new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.LocalApplicationData }
                .Select(p => Path.Combine(Environment.GetFolderPath(p), "Microsoft", "Edge", "Application", "msedge.exe")).FirstOrDefault(File.Exists);
            if (edge == null) throw new FileNotFoundException("Microsoft Edge is not installed in a standard location.");
            string html = Path.Combine(folder, token + ".html");
            await File.WriteAllTextAsync(html, $"<!doctype html><meta charset=utf-8><title>{token}</title><button style='position:absolute;top:30%;left:20%;width:180px;height:100px;background:#6238bb;border:0' onclick=\"document.title='{token}-clicked';this.remove()\">CU test</button>");
            string profile = Path.Combine(Path.GetTempPath(), token + "-Edge");
            if (Directory.Exists(profile)) throw new InvalidOperationException("A browser profile already exists for this run.");
            var window = await Launch(edge, "msedge", "--no-first-run", "--no-default-browser-check", "--user-data-dir=" + profile, "--app=" + new Uri(html).AbsoluteUri);
            await Tool("perform_action", new { request = new ActionRequest(window.WindowRef, "activate") });
            var target = await CaptureTarget(window, Color.FromArgb(98,56,187));
            await DoInput(new(window.WindowRef, "click", ObservationId: target.ObservationId, X:target.X, Y:target.Y));
            await CheckOutcome(() =>
            {
                var title = new StringBuilder(512);
                WindowsComputerUse.Worker.Native.GetWindowText((nint)window.Identity.Handle, title, title.Capacity);
                return title.ToString().Contains(token + "-clicked", StringComparison.Ordinal);
            }, "The local Edge page did not acknowledge the click in its window title.");
            await CloseTestWindow(window);
        });
    }
}
