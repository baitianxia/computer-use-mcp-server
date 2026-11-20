using System.Text.Json;
using WindowsComputerUse.Core;

namespace WindowsComputerUse.Server;

public sealed record GuidanceOptions(string Directory, string SkillsDirectory);

public sealed class GuidanceService : IDisposable
{
    private readonly WorkerClient worker;
    private readonly GuidanceOptions options;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string activePath;
    private string? activeId;
    private FileStream? ownership;

    public GuidanceService(WorkerClient worker, GuidanceOptions options)
    {
        this.worker = worker; this.options = options;
        activePath = Path.Combine(options.Directory, "active.json");
        worker.ExternalActionBlock = () => File.Exists(activePath) ? GuidanceFiles.Read<string>(activePath) : null;
        if (File.Exists(activePath))
        {
            try { AcquireOwnership(); }
            catch (DesktopException) { return; } // A live Claude Code session owns the recorder.
            activeId = GuidanceFiles.Read<string>(activePath);
            var prior = Load(activeId);
            Save(prior with { State = "interrupted", Warning = "MCP restarted. The old recorder is no longer active; resume with a fresh observation or cancel." });
            worker.SuspendActions(activeId);
        }
    }

    private void AcquireOwnership()
    {
        if (ownership is not null) return;
        Directory.CreateDirectory(options.Directory);
        try { ownership = new FileStream(Path.Combine(options.Directory, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new DesktopException("guidance_owned_elsewhere", "Another Claude Code session owns the current guidance. Continue there; this session's desktop actions are paused too."); }
    }

    private string Folder(string id) => GuidanceFiles.Child(options.Directory, id);
    private GuidanceSession Load(string id)
    {
        string path = Path.Combine(Folder(id), "session.json");
        if (!File.Exists(path)) throw new DesktopException("guidance_not_found", "No such guidance session.");
        return GuidanceFiles.Read<GuidanceSession>(path);
    }
    private void Save(GuidanceSession session) => GuidanceFiles.Write(Path.Combine(Folder(session.Id), "session.json"), session);
    private static T Data<T>(WorkerResponse response)
    {
        if (response.Error is not null) throw new DesktopException(response.Error.Code, response.Error.Message, response.Error.Execution, response.Error.Retryable);
        return response.Data!.Value.Deserialize<T>(Wire.Json)!;
    }
    private void Release(string id)
    {
        if (activeId != id) return;
        if (File.Exists(activePath)) File.Delete(activePath);
        worker.ResumeActions(id); activeId = null;
        ownership?.Dispose(); ownership = null;
    }

    public async Task<object> Guide(GuideRequest request, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (activeId is null && File.Exists(activePath))
            {
                try { AcquireOwnership(); } // Only succeeds when the previous MCP has exited.
                catch (DesktopException) when (request.Operation == "status")
                { return new { active = true, session = Load(GuidanceFiles.Read<string>(activePath)), ownedByAnotherSession = true }; }
                activeId = GuidanceFiles.Read<string>(activePath); worker.SuspendActions(activeId);
                var recovered = Load(activeId);
                Save(recovered with { State = "interrupted", Warning = "Previous MCP owner exited. Resume with a fresh observation or cancel." });
            }
            if (request.Operation == "start") return await Start(request, ct);
            if (request.Operation == "status" && request.SessionId is null && activeId is null)
                return new { active = false, sessionsDirectory = options.Directory };
            string id = request.SessionId ?? activeId ?? throw new DesktopException("guidance_not_found", "Supply sessionId.");
            var session = Load(id);
            switch (request.Operation)
            {
                case "status":
                    WorkerResponse? recorder = session.Mode == "demonstration" && activeId == id
                        ? await worker.CallAsync("guidance_status", new { sessionId = id }, 5000, ct) : null;
                    return new { session, active = activeId == id, recorder = recorder?.Data, recorderError = recorder?.Error };
                case "note":
                    GuidanceRules.Text(request.Note, "note");
                    if (request.NoteSource is not ("user" or "assistant")) throw new DesktopException("invalid_argument", "noteSource must be user or assistant.");
                    if (session.Notes.Count >= 100) throw new DesktopException("guidance_limit", "A guidance session accepts up to 100 notes.");
                    var note = new GuidanceNote(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, request.NoteSource, request.Note!);
                    session.Notes.Add(note); Save(session);
                    return new { sessionId = id, note, state = session.State };
                case "resume": return await Resume(session, request.WindowRef, ct);
                case "checkpoint":
                    if (session.State != "resumed" || activeId is not null) throw new DesktopException("guidance_not_ready", "Resume before capturing a result checkpoint.");
                    var checkpoint = await Observe(session, "checkpoint-" + Guid.NewGuid().ToString("N")[..8], ct);
                    Save(session with { LastFrameId = checkpoint.Id });
                    return new { sessionId = id, frame = checkpoint };
                case "cancel":
                    if (activeId == id)
                    {
                        if (session.Mode == "demonstration") await StopRecording(id, ct);
                        Save(session with { State = "cancelled" }); Release(id);
                    }
                    return new { sessionId = id, state = Load(id).State, evidencePreserved = true };
                default: throw new DesktopException("invalid_argument", "operation must be start, status, note, resume, checkpoint or cancel.");
            }
        }
        finally { gate.Release(); }
    }

    private async Task<object> Start(GuideRequest request, CancellationToken ct)
    {
        if (activeId is not null) throw new DesktopException("guidance_active", $"Resume or cancel guidance session {activeId} first.");
        GuidanceRules.Text(request.Task, "task"); GuidanceRules.Text(request.BlockedReason, "blockedReason");
        GuidanceRules.Text(request.WindowRef, "windowRef", 100);
        if (request.Mode is not ("text" or "demonstration") || request.MaxSeconds is < 10 or > 600)
            throw new DesktopException("invalid_argument", "mode must be text or demonstration; maxSeconds must be 10..600.");
        string id = Guid.NewGuid().ToString("N");
        AcquireOwnership();
        if (File.Exists(activePath)) { ownership?.Dispose(); ownership = null; throw new DesktopException("guidance_active", "Another guidance started. Check status."); }
        worker.SuspendActions(id);
        GuidanceSession? session = null;
        try
        {
            var window = Data<WindowInfo>(await worker.CallAsync("describe_window", new { windowRef = request.WindowRef }, 10000, ct));
            session = new(id, request.Task!, request.BlockedReason!, request.Mode, "waiting_for_user", window, DateTimeOffset.UtcNow, []);
            Save(session); activeId = id; GuidanceFiles.Write(activePath, id);
            RecordingStatus? recording = null;
            if (request.Mode == "demonstration")
                recording = Data<RecordingStatus>(await worker.CallAsync("guidance_start", new RecordingRequest(window.WindowRef, id, Folder(id), request.MaxSeconds), 30000, ct));
            else
            {
                var frame = await Observe(session, "before", ct);
                session = session with { LastFrameId = frame.Id }; Save(session);
            }
            return new { session, recording, actionsPaused = true,
                instruction = request.Mode == "demonstration"
                    ? "Ask the user to demonstrate only the blocked step in the selected window or its owned dialogs, then say continue. Ctrl+Alt+F12 stops recording locally. Do not call perform_action before resume."
                    : "Ask one concrete question about the blocked step. Record the user's actual answer with note, then resume and reobserve.",
                evidenceDirectory = Folder(id) };
        }
        catch
        {
            if (session is null) { worker.ResumeActions(id); ownership?.Dispose(); ownership = null; }
            else
            {
                if (session.Mode == "demonstration") worker.Stop();
                Save(session with { State = "interrupted", Warning = "Guidance setup failed. Inspect status; cancel or resume explicitly." });
            }
            throw;
        }
    }

    private async Task<WorkerResponse> StopRecording(string id, CancellationToken ct)
    {
        var result = await worker.CallAsync("guidance_finish", new { sessionId = id }, 20000, ct);
        if (result.Error is not null) worker.Stop(); // Never leave hooks running after handing control back.
        return result;
    }

    private async Task<object> Resume(GuidanceSession session, string? windowRef, CancellationToken ct)
    {
        if (session.State == "resumed" && activeId != session.Id)
            return new { session, actionsPaused = activeId is not null, alreadyResumed = true };
        if (activeId != session.Id) throw new DesktopException("guidance_not_active", "Only the active guidance can resume.");
        WorkerResponse? stopped = session.Mode == "demonstration" ? await StopRecording(session.Id, ct) : null;
        var candidates = Data<WindowInfo[]>(await worker.CallAsync("list_windows", new ListRequest(Limit: 200,
            Selector: new(ProcessName: session.Window.ProcessName)), 15000, ct));
        var current = windowRef is null
            ? candidates.SingleOrDefault(w => w.Identity == session.Window.Identity)
            : candidates.SingleOrDefault(w => w.WindowRef == windowRef);
        if (current is null || !string.Equals(current.ExecutablePath, session.Window.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            throw new DesktopException("guidance_relocation_required", "The target closed or restarted. List windows, identify the same client, then resume with its new windowRef. Actions remain paused.");
        session = session with { Window = current };
        var frame = await Observe(session, "resume-" + Guid.NewGuid().ToString("N")[..8], ct);
        if (frame.Observation is null && frame.Capture is null)
            throw new DesktopException("guidance_observation_failed", "The current UI could not be observed. Actions remain paused; inspect permissions or cancel.");
        session = session with { State = "resumed", ResumedAt = DateTimeOffset.UtcNow, LastFrameId = frame.Id,
            Warning = stopped?.Error?.Message };
        Save(session); Release(session.Id);
        return new { session, actionsPaused = false, frame, recording = stopped?.Data,
            instruction = "Read the guidance notes and demonstration, check the fresh UI, and continue the ORIGINAL task from the current state. Do not replay completed mutations. Save a draft, or publish only after fresh result checks." };
    }

    private async Task<GuidanceFrame> Observe(GuidanceSession session, string id, CancellationToken ct)
    {
        var observed = await worker.CallAsync("inspect_window", new InspectRequest(session.Window.WindowRef, 8, 250), 15000, ct);
        var captured = await worker.CallAsync("capture_window", new CaptureRequest(session.Window.WindowRef, MaxWidth: 1200), 12000, ct);
        var frame = new GuidanceFrame(id, DateTimeOffset.UtcNow,
            observed.Error is null ? observed.Data!.Value.Deserialize<Observation>(Wire.Json) : null,
            captured.Error is null ? captured.Data!.Value.Deserialize<CaptureInfo>(Wire.Json) : null,
            captured.ImageBase64 is not null ? id + ".png" : null,
            new[] { observed.Error?.Message, captured.Error?.Message }.OfType<string>().ToArray());
        string directory = Path.Combine(Folder(session.Id), "frames"); Directory.CreateDirectory(directory);
        if (captured.ImageBase64 is not null) await File.WriteAllBytesAsync(Path.Combine(directory, id + ".png"), Convert.FromBase64String(captured.ImageBase64), ct);
        GuidanceFiles.Write(Path.Combine(directory, id + ".json"), frame);
        return frame;
    }

    public WorkerResponse Read(GuidanceReadRequest request)
    {
        if (request.Offset < 0 || request.Limit is < 1 or > 100) throw new DesktopException("invalid_argument", "offset must be >=0; limit must be 1..100.");
        var session = Load(request.SessionId);
        string folder = Folder(session.Id);
        if (request.FrameId is not null)
        {
            string path = GuidanceFiles.Child(Path.Combine(folder, "frames"), request.FrameId) + ".json";
            if (!File.Exists(path)) throw new DesktopException("guidance_frame_not_found", "No such captured frame.");
            var frame = GuidanceFiles.Read<GuidanceFrame>(path);
            string? image = frame.ImageFile is not null ? Convert.ToBase64String(File.ReadAllBytes(Path.Combine(folder, "frames", frame.ImageFile))) : null;
            return new("", Wire.Element(new { sessionId = session.Id, frame }), ImageBase64: image);
        }
        var events = new List<GuidanceEvent>();
        string eventPath = Path.Combine(folder, "events.jsonl");
        if (File.Exists(eventPath))
        {
            using var stream = new FileStream(eventPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                try { if (JsonSerializer.Deserialize<GuidanceEvent>(line, Wire.Json) is { } item) events.Add(item); }
                catch (JsonException) { break; } // Concurrent last line is not yet complete; the next read retries it.
            }
        }
        var frames = Directory.Exists(Path.Combine(folder, "frames"))
            ? Directory.GetFiles(Path.Combine(folder, "frames"), "*.json").Select(Path.GetFileNameWithoutExtension).Order().ToArray() : [];
        return new("", Wire.Element(new { session, events = events.Skip(request.Offset).Take(request.Limit).ToArray(), totalEvents = events.Count,
            nextOffset = request.Offset + Math.Min(request.Limit, Math.Max(0, events.Count - request.Offset)), frames,
            recordingStatus = File.Exists(Path.Combine(folder, "recording-status.json"))
                ? GuidanceFiles.Read<RecordingStatus>(Path.Combine(folder, "recording-status.json")) : null,
            evidenceDirectory = folder, note = "Demonstration input is evidence of an attempt. Before/after frames are sampled; target selectors are best-effort, not replay instructions." }));
    }

    public async Task<object> SaveLesson(SaveLessonRequest request, CancellationToken ct)
    {
        GuidanceRules.Validate(request.Lesson);
        await gate.WaitAsync(ct);
        try
        {
            var session = Load(request.SessionId);
            GuidanceFiles.Write(Path.Combine(Folder(session.Id), "lesson-draft.json"), request.Lesson);
            if (!request.Publish) return new { status = "draft", installed = false, path = Path.Combine(Folder(session.Id), "lesson-draft.json") };
            if (activeId is not null || session.State != "resumed") throw new DesktopException("guidance_not_ready", "Resume and verify the task before publishing.");
            if (request.Checks?.Length > 10 || (request.Checks is not { Length: >= 1 } && request.UserConfirmationNoteId is null))
                throw new DesktopException("verification_required", "Supply 1..10 fresh UI checks, or an actual user's post-resume confirmation note with captured evidence; otherwise save a draft.");
            if (session.Notes.All(n => n.Source != "user") && (session.Mode != "demonstration"
                || !File.Exists(Path.Combine(Folder(session.Id), "events.jsonl")) || new FileInfo(Path.Combine(Folder(session.Id), "events.jsonl")).Length == 0))
                throw new DesktopException("guidance_not_ready", "No actual user instruction or recorded demonstration has been retained.");
            List<JsonElement> verified = [];
            foreach (var check in request.Checks ?? [])
            {
                WaitRules.Validate(check);
                var target = Data<WindowInfo>(await worker.CallAsync("describe_window", new { windowRef = check.WindowRef }, 10000, ct));
                if (target.Identity != session.Window.Identity) throw new DesktopException("verification_wrong_window", "Verify the resumed guidance window, not an unrelated client.");
                var result = await worker.CallAsync("wait_for", check, check.TimeoutMs + 10000, ct);
                var data = Data<JsonElement>(result);
                if (!data.GetProperty("predicateMatched").GetBoolean())
                    throw new DesktopException("verification_failed", "A requested result check did not match. The draft was preserved; no Skill was installed.");
                var observation = data.GetProperty("observation").Deserialize<Observation>(Wire.Json)!;
                verified.Add(Wire.Element(new { predicateMatched = true, checkedAt = DateTimeOffset.UtcNow,
                    condition = check.Condition, selector = check.Selector, expectedText = check.Text, exact = check.Exact,
                    observedAt = observation.ObservedAt,
                    observed = observation.Elements.Where(e => WaitRules.Matches(observation with { Elements = [e] }, check)).Take(12)
                        .Select(e => new { e.Name, e.AutomationId, e.ControlType, e.Value, e.Text, e.State }).ToArray() }));
            }
            if (request.UserConfirmationNoteId is not null)
            {
                var confirmation = session.Notes.SingleOrDefault(n => n.Id == request.UserConfirmationNoteId && n.Source == "user" && n.At >= session.ResumedAt);
                if (confirmation is null || session.LastFrameId is null)
                    throw new DesktopException("verification_required", "Use a real user confirmation recorded after resume; assistant notes and earlier guidance are not confirmation.");
                var frame = GuidanceFiles.Read<GuidanceFrame>(Path.Combine(Folder(session.Id), "frames", session.LastFrameId + ".json"));
                if (frame.Capture is null || frame.ImageFile is null || !File.Exists(Path.Combine(Folder(session.Id), "frames", frame.ImageFile))
                    || confirmation.At < frame.At) throw new DesktopException("verification_required", "Visual confirmation must follow the saved resume/checkpoint image it verifies.");
                verified.Add(Wire.Element(new { userConfirmed = true, confirmationNoteId = confirmation.Id, confirmation = confirmation.Text,
                    confirmedAt = confirmation.At, frameId = frame.Id, capturedAt = frame.Capture.CapturedAt, source = "human_confirmation_with_capture" }));
            }
            string path = new ClientSkillLibrary(options.SkillsDirectory).Publish(session, request.Lesson, verified.ToArray(), Folder(session.Id));
            return new { status = "published", installed = true, path, skill = request.Lesson.SkillName,
                verification = request.UserConfirmationNoteId is null ? "specified_ui_predicates_matched" : "includes_human_confirmation_with_capture", independentReplayVerified = false,
                instruction = "The personal Claude Code Skill is installed. Read it now; invoke /" + request.Lesson.SkillName + " in a fresh Claude Code session to check reuse." };
        }
        finally { gate.Release(); }
    }

    public void Dispose() { ownership?.Dispose(); gate.Dispose(); }
}
