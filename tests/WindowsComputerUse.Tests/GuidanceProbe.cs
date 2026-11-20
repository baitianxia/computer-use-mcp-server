using System.Diagnostics;
using System.Text.Json;
using WindowsComputerUse.Core;
using WindowsComputerUse.Worker;

internal sealed partial class McpProbe
{
    public static async Task<int> RunGuidance(string[] args)
    {
        string Option(string name)
        {
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : throw new ArgumentException("Missing " + name);
        }
        string reports = Path.GetFullPath(Option("--report-dir"));
        Directory.CreateDirectory(reports);
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("Guidance integration requires Windows; not run."); return 2; }
        Native.SetProcessDpiAwarenessContext((nint)(-4));
        Process? fixture = null, unrelated = null;
        McpProbe? probe = null;
        var completed = new List<object>(); string? failure = null;
        string? activeSession = null;
        async Task<McpProbe> Connect()
        {
            var p = new McpProbe(Option("--server"), reports);
            await p.Request("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "windows-cu-guidance-probe", version = "0.3.0" } });
            await p.server.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            return p;
        }
        try
        {
            probe = await Connect();
            fixture = Process.Start(new ProcessStartInfo(Option("--demo")) { UseShellExecute = false })!;
            var window = await probe.FindWindow("CU Demo Orders", fixture.Id);
            string wr = window.GetProperty("windowRef").GetString()!;
            nint handle = (nint)window.GetProperty("identity").GetProperty("handle").GetInt64();
            async Task<JsonElement> Guide(GuideRequest request, bool error = false) => Payload(await probe.Tool("guide_task", new { request }, error));
            async Task<JsonElement> Inspect(string reference) => Payload(await probe.Tool("inspect_window", new { request = new InspectRequest(reference, 10, 300) }));
            static JsonElement Element(JsonElement observation, string aid) => observation.GetProperty("elements").EnumerateArray().Single(e => e.GetProperty("automationId").GetString() == aid);
            async Task Act(string aid, string action, string? text = null)
            {
                var obs = await Inspect(wr); var e = Element(obs, aid);
                await probe.Tool("perform_action", new { request = new ActionRequest(wr, action, "background_only",
                    obs.GetProperty("observationId").GetString(), e.GetProperty("elementRef").GetString(), Text: text) });
            }
            async Task Click(string reference, string aid)
            {
                var rect = Element(await Inspect(reference), aid).GetProperty("bounds");
                int x = rect.GetProperty("x").GetInt32() + rect.GetProperty("width").GetInt32() / 2;
                int y = rect.GetProperty("y").GetInt32() + rect.GetProperty("height").GetInt32() / 2;
                Native.Send([Native.Move(x, y), Native.Mouse(2), Native.Mouse(4)]);
                await Task.Delay(300);
            }
            async Task Begin(string mode)
            {
                var start = await Guide(new("start", WindowRef: wr, Task: "查询订单并核对结果", BlockedReason: "不知道如何提交订单号", Mode: mode));
                activeSession = start.GetProperty("session").GetProperty("id").GetString()!;
                probe.Record(mode + " guidance starts with model actions paused", start.GetProperty("actionsPaused").GetBoolean(), activeSession);
            }
            async Task Resume()
            {
                var resumed = await Guide(new("resume", activeSession));
                wr = resumed.GetProperty("session").GetProperty("window").GetProperty("windowRef").GetString()!;
                probe.Record("Resume returns fresh UI and original task", resumed.GetProperty("session").GetProperty("task").GetString() == "查询订单并核对结果"
                    && resumed.GetProperty("frame").GetProperty("observation").ValueKind == JsonValueKind.Object
                    && !resumed.GetProperty("actionsPaused").GetBoolean(), "fresh resume observation, retained task");
            }
            await probe.Tool("perform_action", new { request = new ActionRequest(wr, "activate") });
            await Begin("text");
            var refused = Payload(await probe.Tool("perform_action", new { request = new ActionRequest(wr, "activate") }, true));
            probe.Record("Human handoff rejects model input", refused.GetProperty("error").GetProperty("code").GetString() == "guidance_active", "no foreground action delivered");
            await Guide(new("note", activeSession, Note: "填写订单号后点击 Query；等 Completed 并核对订单号。"));
            var trace = Payload(await probe.Tool("read_guidance", new { request = new GuidanceReadRequest(activeSession!) }));
            probe.Record("Actual user instruction is durable", trace.GetProperty("session").GetProperty("notes")[0].GetProperty("text").GetString()!.Contains("Query"), "user-labelled note persisted");
            var image = await probe.Tool("read_guidance", new { request = new GuidanceReadRequest(activeSession!, FrameId: "before") });
            probe.Record("Guidance evidence returns real MCP image content", PngBytes(image).Length > 1000, "before snapshot PNG");
            var lesson = new LessonDraft("windows-client-guidance-demo", "CU Demo Orders", "人工指导查询订单", "CU Demo Orders 的订单查询页",
                ["orderId：用户本次请求的订单号；示范值不能固定复用"],
                [new("填写订单号", "在 OrderNumber 中填写本次 orderId", "输入值等于 orderId", new(AutomationId: "OrderNumber", ControlType: "Edit")),
                 new("查询并等待", "触发 Query，并等新 Completed 标记", "结果订单号匹配，查询序号已更新", new(AutomationId: "Query", ControlType: "Button"))],
                "本次订单号、状态、金额和新的查询序号均一致", ["结果仍旧时继续等待；找不到入口时请求辅导"]);
            var draft = Payload(await probe.Tool("save_client_skill", new { request = new SaveLessonRequest(activeSession!, lesson) }));
            probe.Record("Unverified coaching remains an uninstalled draft", draft.GetProperty("status").GetString() == "draft" && !draft.GetProperty("installed").GetBoolean(), "draft retained on disk");
            await Resume();
            await Act("OrderNumber", "set_value", "A1001"); await Act("Query", "invoke");
            var failedPublish = Payload(await probe.Tool("save_client_skill", new { request = new SaveLessonRequest(activeSession!, lesson, true,
                [new(wr, "Order WRONG", 100, Condition: "text", Selector: new(AutomationId: "QueryResult"))]) }, true));
            probe.Record("Wrong business identifier cannot publish a verified Skill", failedPublish.GetProperty("error").GetProperty("code").GetString() == "verification_failed", "incorrect result check rejected");
            var published = Payload(await probe.Tool("save_client_skill", new { request = new SaveLessonRequest(activeSession!, lesson, true,
                [new(wr, "Order A1001 | Ready", 3000, Condition: "text", Selector: new(AutomationId: "QueryResult")),
                 new(wr, "Completed query #1", 3000, true, "text", new(AutomationId: "QueryStatus"))]) }));
            string skillPath = published.GetProperty("path").GetString()!;
            probe.Record("Fresh result checks publish a personal Claude Skill", File.Exists(skillPath) && published.GetProperty("installed").GetBoolean()
                && !published.GetProperty("independentReplayVerified").GetBoolean(), skillPath);
            probe.Record("Published Skill contains reusable semantic instructions", File.ReadAllText(skillPath).Contains("${CLAUDE_SKILL_DIR}/lessons/")
                && File.ReadAllText(Path.Combine(Path.GetDirectoryName(skillPath)!, "lessons", activeSession + ".md")).Contains("OrderNumber"), "parameterized workflow and provenance");

            await Begin("demonstration");
            Native.EnsureForeground(handle, true);
            await Click(wr, "OrderNumber");
            Native.Send([Native.Key(0x11), Native.Key(0x41), Native.Key(0x41, true), Native.Key(0x11, true)]);
            Native.Send("A1002".SelectMany(c => new[] { Native.Key(c, unicode: true), Native.Key(c, up: true, unicode: true) }).ToArray());
            Native.Send([Native.Key(0x0D), Native.Key(0x0D, true)]);
            await Task.Delay(1300);
            var observed = await Inspect(wr);
            probe.Record("Demonstrated input reaches the client while model input is paused", Element(observed, "QueryResult").GetProperty("name").GetString()!.Contains("A1002"), "test harness SendInput; tagged injected in trace");
            await Click(wr, "ShowDialog");
            var dialog = await probe.FindWindow("CU Demo Dialog", fixture.Id);
            nint dialogHandle = (nint)dialog.GetProperty("identity").GetProperty("handle").GetInt64();
            Native.EnsureForeground(dialogHandle, true);
            await Task.Delay(1000);
            Native.Send([Native.Key(0x0D), Native.Key(0x0D, true)]);
            await Task.Delay(600);
            unrelated = Process.Start(new ProcessStartInfo(Option("--demo")) { UseShellExecute = false })!;
            var other = await probe.FindWindow("CU Demo Orders", unrelated.Id);
            nint otherHandle = (nint)other.GetProperty("identity").GetProperty("handle").GetInt64();
            Native.EnsureForeground(otherHandle, true);
            await Click(other.GetProperty("windowRef").GetString()!, "OrderNumber");
            Native.Send([Native.Key('Z', unicode: true), Native.Key('Z', true, true)]);
            await Task.Delay(400);
            Native.Send([Native.Key(0x11), Native.Key(0x12), Native.Key(0x7B), Native.Key(0x7B, true), Native.Key(0x12, true), Native.Key(0x11, true)]);
            await Task.Delay(500);
            var status = await Guide(new("status", activeSession));
            probe.Record("Local stop shortcut stops recording without resuming model control", !status.GetProperty("recorder").GetProperty("recording").GetBoolean()
                && status.GetProperty("recorder").GetProperty("reason").GetString() == "local_stop_shortcut", status.GetRawText());
            await Resume();
            trace = Payload(await probe.Tool("read_guidance", new { request = new GuidanceReadRequest(activeSession!, Limit: 100) }));
            var events = trace.GetProperty("events").EnumerateArray().ToArray();
            probe.Record("Demonstration records scoped mouse and key events", events.Any(e => e.GetProperty("kind").GetString() == "pointer_down")
                && events.Any(e => e.GetProperty("kind").GetString() == "key"), events.Length + " recorded events");
            probe.Record("Demonstration includes owned dialog input", events.Any(e => e.GetProperty("windowHandle").GetInt64() == dialogHandle.ToInt64()), "owned modal dialog entered with real window identity");
            probe.Record("Unrelated client input is excluded", events.All(e => e.GetProperty("windowHandle").GetInt64() != otherHandle.ToInt64()), "second process was clicked and typed into");
            probe.Record("Synthetic test input is labelled and text keystrokes are not stored", events.Any(e => e.GetProperty("injected").GetBoolean())
                && events.Where(e => e.GetProperty("kind").GetString() == "text_input").All(e => !e.GetProperty("detail").GetString()!.Contains("A1002")), "result values may appear in scoped UIA/screenshots");
            probe.Record("Semantic target and before/after evidence retained", events.Any(e => e.TryGetProperty("target", out var t) && t.TryGetProperty("automationId", out var a)
                && a.GetString() == "OrderNumber") && events.Any(e => e.TryGetProperty("afterFrameId", out _)), "best-effort mapping from prior UIA sample");
            await probe.Tool("save_client_skill", new { request = new SaveLessonRequest(activeSession!, lesson with { Summary = "示范 Enter 提交订单查询" }, true,
                [new(wr, "Order A1002 | Shipped", 3000, Condition: "text", Selector: new(AutomationId: "QueryResult"))]) });
            probe.Record("Repeated coaching accumulates lessons without replacing earlier guidance", Directory.GetFiles(Path.Combine(Path.GetDirectoryName(skillPath)!, "lessons"), "*.md").Length == 2, "two sessions retained");

            await Begin("text");
            await Guide(new("note", activeSession, Note: "保留这段指导以验证重启恢复。"));
            completed.AddRange(probe.checks); probe.Dispose();
            probe = await Connect();
            var interrupted = await Guide(new("status"));
            probe.Record("MCP restart recovers interrupted coaching", interrupted.GetProperty("session").GetProperty("state").GetString() == "interrupted", "durable session and action barrier restored");
            refused = Payload(await probe.Tool("perform_action", new { request = new ActionRequest(wr, "activate") }, true));
            probe.Record("Restart cannot silently reclaim human control", refused.GetProperty("error").GetProperty("code").GetString() == "guidance_active", "old references not acted on");
            await Resume();
            trace = Payload(await probe.Tool("read_guidance", new { request = new GuidanceReadRequest(activeSession!) }));
            probe.Record("Guidance and installed Skills survive a new MCP session", trace.GetProperty("session").GetProperty("notes").GetArrayLength() == 1 && File.Exists(skillPath), "file persistence; model Skill activation is a separate manual test");
            await Begin("text");
            fixture.Kill(); await fixture.WaitForExitAsync(); fixture.Dispose(); fixture = null;
            var stale = await Guide(new("resume", activeSession), true);
            probe.Record("Closed target keeps handoff paused pending relocation", stale.GetProperty("error").GetProperty("code").GetString() == "guidance_relocation_required", "no replay against stale window");
            fixture = Process.Start(new ProcessStartInfo(Option("--demo")) { UseShellExecute = false })!;
            var reopened = await probe.FindWindow("CU Demo Orders", fixture.Id);
            var relocated = await Guide(new("resume", activeSession, reopened.GetProperty("windowRef").GetString()));
            wr = relocated.GetProperty("session").GetProperty("window").GetProperty("windowRef").GetString()!;
            probe.Record("Same client can be explicitly relocated after restart", relocated.GetProperty("session").GetProperty("window").GetProperty("identity").GetProperty("processId").GetInt32() == fixture.Id, "fresh process identity");
            await Begin("text");
            var cancel = await Guide(new("cancel", activeSession));
            probe.Record("Cancel preserves guidance evidence and releases handoff", cancel.GetProperty("state").GetString() == "cancelled" && cancel.GetProperty("evidencePreserved").GetBoolean(), "no deletion or rollback implied");
            await probe.Tool("perform_action", new { request = new ActionRequest(wr, "activate") });
            await Begin("text");
            var beforeNote = await Guide(new("note", activeSession, Note: "这是操作说明，还未确认结果。"));
            await Resume();
            var checkpoint = await Guide(new("checkpoint", activeSession));
            probe.Record("Checkpoint captures current visual result", checkpoint.GetProperty("frame").GetProperty("capture").ValueKind == JsonValueKind.Object, "fresh image for visual-only confirmation");
            var wrongConfirmation = Payload(await probe.Tool("save_client_skill", new { request = new SaveLessonRequest(activeSession!,
                lesson with { SkillName = "windows-client-visual-demo" }, true, UserConfirmationNoteId: beforeNote.GetProperty("note").GetProperty("id").GetString()) }, true));
            probe.Record("Earlier instruction cannot masquerade as result confirmation", wrongConfirmation.GetProperty("error").GetProperty("code").GetString() == "verification_required", "confirmation must follow result screenshot");
            var confirmed = await Guide(new("note", activeSession, Note: "测试确认：当前截图显示新打开的 CU Demo Orders 空查询界面。"));
            var visual = Payload(await probe.Tool("save_client_skill", new { request = new SaveLessonRequest(activeSession!,
                lesson with { SkillName = "windows-client-visual-demo", Summary = "截图结果人工确认测试" }, true,
                UserConfirmationNoteId: confirmed.GetProperty("note").GetProperty("id").GetString()) }));
            probe.Record("Visual-only publication retains explicit human confirmation source", visual.GetProperty("verification").GetString() == "includes_human_confirmation_with_capture", "protocol fixture models a user confirmation; actual human test still required");
            var limited = await Guide(new("start", WindowRef: wr, Task: "录制超时测试", BlockedReason: "验证限时停止", Mode: "demonstration", MaxSeconds: 10));
            activeSession = limited.GetProperty("session").GetProperty("id").GetString()!;
            await Task.Delay(11000);
            var expired = await Guide(new("status", activeSession));
            probe.Record("Recording time limit stops capture but retains handoff", !expired.GetProperty("recorder").GetProperty("recording").GetBoolean()
                && expired.GetProperty("recorder").GetProperty("reason").GetString() == "duration_limit" && expired.GetProperty("active").GetBoolean(), "10 second cap; no automatic resume");
            await Guide(new("cancel", activeSession));
            return 0;
        }
        catch (Exception ex) { failure = ex.ToString(); Console.Error.WriteLine(failure); return 1; }
        finally
        {
            if (probe is not null)
            {
                if (activeSession is not null) { try { await probe.Tool("guide_task", new { request = new GuideRequest("cancel", activeSession) }); } catch { } }
                completed.AddRange(probe.checks); probe.Dispose();
            }
            foreach (var process in new[] { fixture, unrelated })
                if (process is not null) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } finally { process.Dispose(); } }
            await File.WriteAllTextAsync(Path.Combine(reports, "windows-guidance.json"), JsonSerializer.Serialize(new
            {
                sourceCommit = Environment.GetEnvironmentVariable("WINDOWS_CU_SOURCE_COMMIT"), passed = failure is null, failure,
                scope = "native coaching tools, injected demonstration, persistence and personal Skill publication", checks = completed,
                actualHumanDemonstrationVerified = false, claudeModelVerified = false,
                remaining = new[] { "Actual human coaching in Claude Code", "Model synthesis and automatic Skill matching", "Real client workflow and independent replay", "Windows 10/11 workstation input hooks" }
            }, new JsonSerializerOptions(Wire.Json) { WriteIndented = true }));
        }
    }
}
