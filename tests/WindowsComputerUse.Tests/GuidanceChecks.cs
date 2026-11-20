using System.Text.Json;
using WindowsComputerUse.Core;
using WindowsComputerUse.Server;

internal static class GuidanceChecks
{
    internal static async Task Run(Action<string, Action> check, Func<string, Func<Task>, Task> checkAsync, WorkerOptions fake)
    {
        static void Assert(bool condition, string why) { if (!condition) throw new Exception(why); }
        static void Error(string expected, Action action)
        {
            try { action(); throw new Exception("Expected " + expected); }
            catch (DesktopException ex) { Assert(ex.Error.Code == expected, ex.Message); }
        }
        string root = Path.Combine(Path.GetTempPath(), "windows-cu-guidance-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var session = new GuidanceSession("sample", "Query order", "Cannot find entry", "text", "resumed",
            new("w", new(12, 34, 56), "Orders", "Orders", "C:\\orders.exe", 0, new(), false, false, "OrderForm"),
            DateTimeOffset.UtcNow, [new("n1", DateTimeOffset.UtcNow, "user", "Click Query")], ResumedAt: DateTimeOffset.UtcNow);
        var draft = new LessonDraft("windows-client-orders", "订单客户端", "查询订单", "在订单页查询", ["orderId: 本次订单号"],
            [new("查询", "重新定位 Query 按钮并触发", "查询状态变成 Completed", new(AutomationId: "Query"))],
            "核对本次订单号、刷新标识和状态", ["找不到入口时请求用户指导"]);
        JsonElement[] verified = [Wire.Element(new { predicateMatched = true, expectedText = "Completed", checkedAt = DateTimeOffset.UtcNow })];
        try
        {
            check("guidance paths and skill names cannot escape their roots", () =>
            {
                Error("invalid_argument", () => GuidanceFiles.Child(root, "../escape"));
                Error("invalid_argument", () => GuidanceRules.Validate(draft with { SkillName = "../escape" }));
                Error("invalid_argument", () => GuidanceRules.Validate(draft with { Steps = [] }));
                GuidanceRules.Validate(draft);
            });
            check("skill publication requires real guidance provenance and matching check results", () =>
            {
                var library = new ClientSkillLibrary(Path.Combine(root, "claude", "skills"));
                Error("guidance_not_ready", () => library.Publish(session with { State = "waiting_for_user" }, draft, verified, root));
                Error("guidance_not_ready", () => library.Publish(session with { Notes = [] }, draft, verified, root));
                Error("verification_required", () => library.Publish(session, draft, [], root));
                Error("verification_required", () => library.Publish(session, draft, [Wire.Element(new { predicateMatched = false })], root));
            });
            check("personal Claude Skill publication is idempotent, cumulative and outside install versions", () =>
            {
                var library = new ClientSkillLibrary(Path.Combine(root, "claude", "skills"));
                string path = library.Publish(session, draft, verified, root);
                library.Publish(session, draft, verified, root);
                library.Publish(session with { Id = "second" }, draft with { Summary = "另一种查询" }, verified, root);
                var index = GuidanceFiles.Read<SkillIndex>(Path.Combine(Path.GetDirectoryName(path)!, "windows-cu-lessons.json"));
                Assert(index.Lessons.Length == 2, "Same session duplicated, or previous lesson lost.");
                string skill = File.ReadAllText(path);
                Assert(skill.StartsWith("---\nname: windows-client-orders\ndescription: ") && skill.Contains("$ARGUMENTS")
                    && skill.Contains("${CLAUDE_SKILL_DIR}/lessons/second.md"), "Invalid Claude Code Skill layout.");
                Assert(Directory.GetDirectories(Path.Combine(root, "claude", "skills")).Length == 1, "Backups are discoverable as duplicate Skills.");
                Assert(Directory.GetDirectories(Path.Combine(root, "claude", "windows-cu-skill-backups")).Length == 2, "Prior managed versions not backed up.");
            });
            check("publication preserves unrelated Skills and externally edited lessons", () =>
            {
                string skills = Path.Combine(root, "claude", "skills");
                var library = new ClientSkillLibrary(skills);
                string unrelated = Path.Combine(skills, "windows-client-unrelated"); Directory.CreateDirectory(unrelated);
                File.WriteAllText(Path.Combine(unrelated, "SKILL.md"), "User content");
                Error("skill_conflict", () => library.Publish(session, draft with { SkillName = "windows-client-unrelated" }, verified, root));
                Assert(File.ReadAllText(Path.Combine(unrelated, "SKILL.md")) == "User content", "User Skill overwritten.");
                string lesson = Path.Combine(skills, draft.SkillName, "lessons", "sample.md");
                File.AppendAllText(lesson, "\nUser correction");
                Error("skill_conflict", () => library.Publish(session, draft, verified, root));
                Assert(File.ReadAllText(lesson).EndsWith("User correction"), "Manual correction lost.");
            });
            await checkAsync("handoff rejects queued model mutations while allowing read-only calls", async () =>
            {
                using var worker = new WorkerClient(fake);
                worker.SuspendActions("session");
                var action = await worker.CallAsync("perform_action", new { }, 200, default);
                Assert(action.Error?.Code == "guidance_active" && action.Error.Execution == "not_executed", "Input reached the worker during human control.");
                Assert((await worker.CallAsync("echo", new { }, 5000, default)).Error is null, "Read blocked during coaching.");
                worker.ResumeActions("wrong-session");
                Assert((await worker.CallAsync("perform_action", new { }, 200, default)).Error?.Code == "guidance_active", "Wrong session released control.");
                worker.ResumeActions("session");
                Assert((await worker.CallAsync("perform_action", new { }, 200, default)).Error?.Execution == "unknown", "Resume did not release action barrier.");
            });
            await checkAsync("restart restores paused guidance and preserves drafts across service instances", async () =>
            {
                string data = Path.Combine(root, "restart");
                GuidanceFiles.Write(Path.Combine(data, session.Id, "session.json"), session with { State = "waiting_for_user" });
                GuidanceFiles.Write(Path.Combine(data, "active.json"), session.Id);
                using var worker = new WorkerClient(fake);
                using var service = new GuidanceService(worker, new(data, Path.Combine(root, "new-skills")));
                Assert((await worker.CallAsync("perform_action", new { }, 200, default)).Error?.Code == "guidance_active", "Restart silently resumed control.");
                var read = service.Read(new(session.Id));
                Assert(read.Data!.Value.GetProperty("session").GetProperty("state").GetString() == "interrupted", "Interrupted state not retained.");
                await service.SaveLesson(new(session.Id, draft), default);
                Assert(File.Exists(Path.Combine(data, session.Id, "lesson-draft.json")) && !Directory.Exists(Path.Combine(root, "new-skills")), "Draft installed prematurely.");
                await service.Guide(new("cancel", session.Id), default);
                Assert(!File.Exists(Path.Combine(data, "active.json")), "Cancel left the handoff active.");
            });
            await checkAsync("another Claude session cannot seize live guidance and can recover after owner exit", async () =>
            {
                string data = Path.Combine(root, "multiple");
                GuidanceFiles.Write(Path.Combine(data, session.Id, "session.json"), session with { State = "waiting_for_user" });
                GuidanceFiles.Write(Path.Combine(data, "active.json"), session.Id);
                using var firstWorker = new WorkerClient(fake); using var secondWorker = new WorkerClient(fake);
                var first = new GuidanceService(firstWorker, new(data, Path.Combine(root, "skills-first")));
                using var second = new GuidanceService(secondWorker, new(data, Path.Combine(root, "skills-second")));
                Assert((await secondWorker.CallAsync("perform_action", new { }, 200, default)).Error?.Code == "guidance_active", "Another MCP bypassed human control.");
                try { await second.Guide(new("cancel", session.Id), default); throw new Exception("Live ownership stolen."); }
                catch (DesktopException ex) { Assert(ex.Error.Code == "guidance_owned_elsewhere", "Unexpected ownership error."); }
                first.Dispose();
                await second.Guide(new("cancel", session.Id), default);
                Assert(!File.Exists(Path.Combine(data, "active.json")), "Dead session not recoverable.");
            });
            await checkAsync("visual-only learning preserves human confirmation provenance and rejects stale confirmation", async () =>
            {
                string data = Path.Combine(root, "visual");
                string skills = Path.Combine(root, "visual-claude", "skills");
                var observedAt = DateTimeOffset.UtcNow;
                var capture = new CaptureInfo("o", "w", observedAt, "wgc", new(0, 0, 100, 100), 100, 100, []);
                var frame = new GuidanceFrame("checkpoint", observedAt, null, capture, "checkpoint.png", []);
                var confirmed = session with { LastFrameId = frame.Id, ResumedAt = observedAt.AddSeconds(-1), Notes =
                    [new("old", observedAt.AddSeconds(-10), "user", "earlier instruction"),
                     new("guess", observedAt.AddSeconds(1), "assistant", "I think it worked"),
                     new("confirmed", observedAt.AddSeconds(2), "user", "我确认截图里的联系人和新发送消息正确")] };
                GuidanceFiles.Write(Path.Combine(data, confirmed.Id, "session.json"), confirmed);
                GuidanceFiles.Write(Path.Combine(data, confirmed.Id, "frames", frame.Id + ".json"), frame);
                File.WriteAllBytes(Path.Combine(data, confirmed.Id, "frames", frame.ImageFile!), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII="));
                using var worker = new WorkerClient(fake); using var service = new GuidanceService(worker, new(data, skills));
                foreach (string note in new[] { "old", "guess" })
                {
                    try { await service.SaveLesson(new(confirmed.Id, draft, true, UserConfirmationNoteId: note), default); throw new Exception("Invalid confirmation accepted."); }
                    catch (DesktopException ex) { Assert(ex.Error.Code == "verification_required", ex.Message); }
                }
                var result = Wire.Element(await service.SaveLesson(new(confirmed.Id, draft, true, UserConfirmationNoteId: "confirmed"), default));
                Assert(result.GetProperty("verification").GetString() == "includes_human_confirmation_with_capture"
                    && !result.GetProperty("independentReplayVerified").GetBoolean(), "Human confirmation misreported as model replay.");
            });
        }
        finally { Directory.Delete(root, true); }
    }
}
