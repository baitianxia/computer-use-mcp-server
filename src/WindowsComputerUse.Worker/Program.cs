using System.Text;
using System.Text.Json;
using WindowsComputerUse.Core;

namespace WindowsComputerUse.Worker;

internal static class Program
{
    [MTAThread]
    private static void Main()
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        Native.SetProcessDpiAwarenessContext((nint)(-4));
        using var session = new DesktopSession();
        string? line;
        while ((line = Console.ReadLine()) is not null)
        {
            WorkerRequest? request = null;
            WorkerResponse response;
            try
            {
                request = JsonSerializer.Deserialize<WorkerRequest>(line, Wire.Json) ?? throw new JsonException("Empty request.");
                response = Dispatch(session, request);
            }
            catch (DesktopException ex) { response = new(request?.Id ?? "", Error: ex.Error); }
            catch (JsonException ex) { response = new(request?.Id ?? "", Error: new("invalid_argument", ex.Message)); }
            catch (Exception ex)
            {
                response = new(request?.Id ?? "", Error: new("desktop_error", $"{ex.GetType().Name}: {ex.Message}", request?.Operation == "perform_action" ? "unknown" : "not_executed"));
            }
            Console.WriteLine(JsonSerializer.Serialize(response, Wire.Json));
        }
    }

    private static T Args<T>(WorkerRequest request) => request.Arguments.Deserialize<T>(Wire.Json) ?? throw new JsonException("Arguments required.");

    private static WorkerResponse Dispatch(DesktopSession session, WorkerRequest request)
    {
        switch (request.Operation)
        {
            case "list_windows": return new(request.Id, Wire.Element(session.List(Args<ListRequest>(request))));
            case "describe_window": return new(request.Id, Wire.Element(session.Describe(request.Arguments.GetProperty("windowRef").GetString()!)));
            case "guidance_start": return new(request.Id, Wire.Element(session.StartGuidance(Args<RecordingRequest>(request))));
            case "guidance_status": return new(request.Id, Wire.Element(session.GuidanceStatus(request.Arguments.GetProperty("sessionId").GetString()!)));
            case "guidance_finish": return new(request.Id, Wire.Element(session.FinishGuidance(request.Arguments.GetProperty("sessionId").GetString()!)));
            case "inspect_window": return new(request.Id, Wire.Element(session.Inspect(Args<InspectRequest>(request))));
            case "capture_window":
                var capture = session.Capture(Args<CaptureRequest>(request));
                return new(request.Id, Wire.Element(capture.Info), ImageBase64: capture.Image);
            case "perform_action": return new(request.Id, Wire.Element(session.Act(Args<ActionRequest>(request))));
            case "wait_for": return new(request.Id, Wire.Element(session.Wait(Args<WaitRequest>(request))));
            default: throw new DesktopException("unknown_operation", "Unsupported worker operation.");
        }
    }
}
