using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WindowsComputerUse.Core;

namespace WindowsComputerUse.Server;

[McpServerToolType]
public sealed class DesktopTools
{
    [McpServerTool(Name = "list_windows", ReadOnly = true), Description("List interactive Windows windows with process identities and short-lived windowRef values. query is a case-insensitive substring of title/process/path, not a semantic search. Select by multiple signals; ask the user if candidates remain ambiguous. Re-list after restart or a stale-window error.")]
    public static async Task<CallToolResult> ListWindows(WorkerClient worker, string? query = null, int limit = 50, CancellationToken cancellationToken = default)
        => Result(await worker.CallAsync("list_windows", new ListRequest(query, limit), 15000, cancellationToken));

    [McpServerTool(Name = "inspect_window", ReadOnly = true), Description("Read a bounded UI Automation control tree. Use the returned observationId and elementRef for UIA actions. References expire after two minutes and are invalidated by an action on that window. Inspect again after changes; controls in truncated/virtualized areas may be absent. Does not activate the window.")]
    public static async Task<CallToolResult> InspectWindow(WorkerClient worker, InspectRequest request, CancellationToken cancellationToken = default)
        => Result(await worker.CallAsync("inspect_window", request, 15000, cancellationToken));

    [McpServerTool(Name = "capture_window", ReadOnly = true), Description("Return PNG image and physical coordinate metadata. Default auto tries Windows Graphics Capture, then PrintWindow on supported fallback errors; neither activates the window. method reports the actual backend, requestedMethod the request, fallbackError the WGC failure if fallback occurred. Explicit wgc never falls back. Explicit screen requires the target already foreground and may include overlaps. Minimized windows are unsupported. Pointer x/y are returned-image pixels and require this capture's observationId. New capture frames do not prove business freshness or success.")]
    public static async Task<CallToolResult> CaptureWindow(WorkerClient worker, CaptureRequest request, CancellationToken cancellationToken = default)
    {
        try { CapturePolicy.Validate(request); }
        catch (DesktopException ex) { return Result(new("", Error: ex.Error)); }
        return Result(await worker.CallAsync("capture_window", request, 12000, cancellationToken));
    }

    [McpServerTool(Name = "perform_action"), Description("Perform ONE desktop action, then return a fresh UIA observation. UIA actions: invoke, set_value (empty text clears), toggle, select, expand, collapse; require elementRef + observationId from inspect_window. Foreground actions: activate, click, double_click, move, drag, scroll, type_text, press_keys. Pointer actions use an inspected elementRef OR capture image x/y coordinates; drag uses capture x/y/toX/toY. type_text/press_keys optionally focus an inspected elementRef, otherwise use existing focus in the target. mode background_only rejects all foreground input and never silently falls back. Keys is one chord, e.g. [CTRL,A]; scrollDelta is signed wheel units (120 per notch). Result execution=executed means only the action returned; business verification remains not_verified. Reobserve after errors and do not repeat uncertain mutations blindly.")]
    public static async Task<CallToolResult> PerformAction(WorkerClient worker, ActionRequest request, CancellationToken cancellationToken = default)
    {
        try { ActionRules.Validate(request); }
        catch (DesktopException ex) { return Result(new("", Error: ex.Error)); }
        return Result(await worker.CallAsync("perform_action", request, 20000, cancellationToken));
    }

    [McpServerTool(Name = "wait_for", ReadOnly = true), Description("Poll bounded UIA snapshots for matching name/value/text. This tests a visible predicate, not business freshness or task completion. Check the exact requested identifier and client-specific completion evidence separately. timeoutMs 100..30000; returns the last observation.")]
    public static async Task<CallToolResult> WaitFor(WorkerClient worker, WaitRequest request, CancellationToken cancellationToken = default)
        => Result(await worker.CallAsync("wait_for", request, Math.Clamp(request.TimeoutMs, 100, 30000) + 10000, cancellationToken));

    [McpServerTool(Name = "stop"), Description("Stop the current desktop worker and invalidate its references. Can run while a desktop call is blocked. Already-delivered UI actions are not undone. Does not exit Claude Code; re-list windows to begin another operation.")]
    public static CallToolResult Stop(WorkerClient worker) => Result(new("", Wire.Element(worker.Stop())));

    public static CallToolResult Result(WorkerResponse response)
    {
        JsonElement payload = response.Error is null ? Wire.Element(new { data = response.Data }) : Wire.Element(new { error = response.Error });
        var content = new List<ContentBlock> { new TextContentBlock { Text = payload.GetRawText() } };
        if (response.ImageBase64 is not null) content.Add(ImageContentBlock.FromBytes(Convert.FromBase64String(response.ImageBase64), "image/png"));
        return new() { IsError = response.Error is not null, Content = content, StructuredContent = payload };
    }
}
