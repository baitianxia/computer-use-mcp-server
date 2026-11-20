namespace WindowsComputerUse.Core;

public static class Selectors
{
    private static bool Same(string? actual, string? expected) => expected is null || string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    public static bool Matches(WindowInfo window, WindowSelector? s) => s is null ||
        (Same(window.ProcessName, s.ProcessName) && Same(window.ExecutablePath, s.ExecutablePath)
        && Same(window.ClassName, s.ClassName) && (s.ProcessId is null || window.Identity.ProcessId == s.ProcessId)
        && (s.TitleContains is null || window.Title.Contains(s.TitleContains, StringComparison.OrdinalIgnoreCase)));
    public static bool Matches(ElementInfo element, ElementSelector? s) => s is null ||
        (Same(element.AutomationId, s.AutomationId) && Same(element.Name, s.Name)
        && Same(element.ControlType, s.ControlType) && Same(element.ClassName, s.ClassName)
        && (s.NameContains is null || element.Name.Contains(s.NameContains, StringComparison.OrdinalIgnoreCase)));
}

public static class WaitRules
{
    public static void Validate(WaitRequest request)
    {
        if (request.TimeoutMs is < 100 or > 30000 || request.MaxDepth is < 0 or > 12 || request.MaxNodes is < 1 or > 500)
            throw new DesktopException("invalid_argument", "timeoutMs 100..30000, maxDepth 0..12 and maxNodes 1..500 are required.");
        if (request.Condition is not ("text" or "exists" or "absent" or "enabled" or "disabled" or "value_equals" or "selected" or "unselected" or "toggle_on" or "toggle_off" or "expanded" or "collapsed"))
            throw new DesktopException("invalid_argument", "Unknown wait condition.");
        if (request.Condition == "text" && string.IsNullOrEmpty(request.Text) || request.Condition == "value_equals" && request.Text is null)
            throw new DesktopException("invalid_argument", "Supply text for this wait condition.");
        if (request.Condition != "text" && request.Selector is null)
            throw new DesktopException("invalid_argument", "State conditions require an element selector.");
    }

    public static bool Matches(Observation observation, WaitRequest request)
    {
        var elements = observation.Elements.Where(e => Selectors.Matches(e, request.Selector)).ToArray();
        if (request.Condition == "absent") return elements.Length == 0 && !observation.Truncated;
        return elements.Any(e => request.Condition switch
        {
            "text" => new[] { e.Name, e.Value, e.Text }.Any(s => s is not null &&
                (request.Exact ? s == request.Text : s.Contains(request.Text!, StringComparison.Ordinal))),
            "exists" => true,
            "enabled" => e.Enabled,
            "disabled" => !e.Enabled,
            "value_equals" => e.Value == request.Text,
            "selected" => e.State?.Selected == true,
            "unselected" => e.State?.Selected == false,
            "toggle_on" => e.State?.Toggle == "on",
            "toggle_off" => e.State?.Toggle == "off",
            "expanded" => e.State?.ExpandCollapse == "expanded",
            "collapsed" => e.State?.ExpandCollapse == "collapsed",
            _ => false
        });
    }
}
