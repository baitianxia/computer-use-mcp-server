namespace WindowsComputerUse.Core;

public sealed record GuideRequest(string Operation, string? SessionId = null, string? WindowRef = null,
    string? Task = null, string? BlockedReason = null, string Mode = "text", string? Note = null,
    string NoteSource = "user", int MaxSeconds = 300);
public sealed record GuidanceReadRequest(string SessionId, int Offset = 0, int Limit = 30, string? FrameId = null);
public sealed record GuidanceNote(string Id, DateTimeOffset At, string Source, string Text);
public sealed record GuidanceSession(string Id, string Task, string BlockedReason, string Mode, string State,
    WindowInfo Window, DateTimeOffset StartedAt, List<GuidanceNote> Notes, string? LastFrameId = null,
    DateTimeOffset? ResumedAt = null, string? Warning = null);
public sealed record GuidanceFrame(string Id, DateTimeOffset At, Observation? Observation,
    CaptureInfo? Capture, string? ImageFile, string[] Warnings);
public sealed record GuidanceEvent(int Sequence, DateTimeOffset At, string Kind, string Detail,
    long WindowHandle, int? X, int? Y, bool Injected, string? BeforeFrameId,
    string? AfterFrameId, ElementSelector? Target, string TargetSource = "unresolved");
public sealed record RecordingRequest(string WindowRef, string SessionId, string Directory, int MaxSeconds = 300);
public sealed record RecordingStatus(string SessionId, bool Recording, string Reason, int Events, int Frames,
    int DroppedEvents, string[] Warnings);
public sealed record LessonStep(string Goal, string Instruction, string ExpectedResult,
    ElementSelector? Selector = null);
public sealed record LessonDraft(string SkillName, string ClientName, string Summary, string AppliesWhen,
    string[] Parameters, LessonStep[] Steps, string SuccessCriteria, string[] Recovery);
public sealed record SaveLessonRequest(string SessionId, LessonDraft Lesson, bool Publish = false,
    WaitRequest[]? Checks = null, string? UserConfirmationNoteId = null);

public static class GuidanceRules
{
    public static void Text(string? value, string field, int max = 4000)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Contains('\0'))
            throw new DesktopException("invalid_argument", $"{field} must contain 1..{max} nonempty characters.");
    }

    public static void Validate(LessonDraft lesson)
    {
        if (lesson is null) throw new DesktopException("invalid_argument", "lesson is required.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(lesson.SkillName ?? "", "^windows-client-[a-z0-9][a-z0-9-]{0,47}$"))
            throw new DesktopException("invalid_argument", "skillName must be windows-client- followed by 1..48 lowercase letters, digits or hyphens.");
        Text(lesson.ClientName, "clientName", 100); Text(lesson.Summary, "summary", 500);
        Text(lesson.AppliesWhen, "appliesWhen"); Text(lesson.SuccessCriteria, "successCriteria");
        if (lesson.Steps is not { Length: >= 1 and <= 30 } || lesson.Parameters is null || lesson.Parameters.Length > 30
            || lesson.Recovery is null || lesson.Recovery.Length > 20)
            throw new DesktopException("invalid_argument", "Supply 1..30 steps, at most 30 parameters and 20 recovery notes.");
        foreach (var step in lesson.Steps)
        {
            if (step is null) throw new DesktopException("invalid_argument", "A lesson step cannot be null.");
            Text(step.Goal, "step.goal", 500); Text(step.Instruction, "step.instruction"); Text(step.ExpectedResult, "step.expectedResult");
        }
        foreach (var value in lesson.Parameters.Concat(lesson.Recovery)) Text(value, "lesson note");
    }
}
