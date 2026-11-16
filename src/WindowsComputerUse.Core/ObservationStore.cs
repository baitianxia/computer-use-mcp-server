namespace WindowsComputerUse.Core;

/// <summary>References are owned by an observation, expire, and never silently resolve to another element.</summary>
public sealed class ObservationStore<T>(TimeSpan lifetime, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, Entry> entries = [];
    private sealed record Entry(string WindowRef, DateTimeOffset CreatedAt, Dictionary<string, T> Elements, CaptureInfo? Capture);

    public string Add(string windowRef, Dictionary<string, T>? elements = null, CaptureInfo? capture = null)
    {
        Prune();
        // Retain a small bounded set; reads alone must not keep old UI references alive forever.
        if (entries.Count >= 32) entries.Remove(entries.MinBy(e => e.Value.CreatedAt).Key);
        string id = Guid.NewGuid().ToString("N");
        entries[id] = new(windowRef, now(), elements ?? [], capture);
        return id;
    }

    public T Element(string windowRef, string observationId, string elementRef)
    {
        var entry = Get(windowRef, observationId);
        return entry.Elements.TryGetValue(elementRef, out var value) ? value
            : throw new DesktopException("stale_element", "Element does not belong to this observation. Inspect the window again.");
    }

    public CaptureInfo Capture(string windowRef, string observationId) => Get(windowRef, observationId).Capture
        ?? throw new DesktopException("capture_required", "Image coordinates require an observationId returned by capture_window.");

    public void InvalidateWindow(string windowRef)
    {
        foreach (var key in entries.Where(e => e.Value.WindowRef == windowRef).Select(e => e.Key).ToArray()) entries.Remove(key);
    }

    private Entry Get(string windowRef, string id)
    {
        Prune();
        if (!entries.TryGetValue(id, out var entry) || entry.WindowRef != windowRef)
            throw new DesktopException("stale_observation", "Observation expired, was invalidated by an action, or belongs to another window. Observe again.");
        return entry;
    }

    private void Prune()
    {
        foreach (var key in entries.Where(e => now() - e.Value.CreatedAt > lifetime).Select(e => e.Key).ToArray()) entries.Remove(key);
    }
}
