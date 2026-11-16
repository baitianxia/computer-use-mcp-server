using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using WindowsComputerUse.Core;

internal sealed class McpProbe : IDisposable
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
            foreach (var name in new[] { "list_windows", "inspect_window", "capture_window", "perform_action", "wait_for", "stop" })
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
            await probe.Tool("stop", new { });
            var stale = Payload(await probe.Tool("inspect_window", new { request = new InspectRequest(windowRef) }, true));
            probe.Record("Restart invalidates old window references", stale.GetProperty("error").GetProperty("code").GetString() == "stale_window", "old reference rejected after worker stop");
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
                remaining = new[] { "Claude Code /mcp and Skill discovery", "Claude-driven visual fallback", "WGC image content, occlusion, multi-monitor DPI and resize validation", "Continuous foreground noninterference", "Real business client acceptance" } };
            await File.WriteAllTextAsync(Path.Combine(reportDirectory, desktop ? "windows-smoke.json" : "protocol-smoke.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions(Wire.Json) { WriteIndented = true }));
        }
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    public void Dispose() { try { if (!server.HasExited) server.Kill(entireProcessTree: true); } finally { server.Dispose(); } }
}
