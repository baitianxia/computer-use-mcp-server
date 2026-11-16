using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WindowsComputerUse.Core;

namespace WindowsComputerUse.Server;

public sealed record WorkerOptions(string FileName, string[] Arguments, bool RequireWindows = true);

/// <summary>Serializes access to one desktop session. A wedged provider can be terminated without wedging MCP.</summary>
public sealed class WorkerClient(WorkerOptions options, Action<object>? audit = null) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object processLock = new();
    private Process? process;
    private CancellationTokenSource? active;

    public async Task<WorkerResponse> CallAsync(string operation, object arguments, int timeoutMs, CancellationToken cancellationToken)
    {
        string id = Guid.NewGuid().ToString("N");
        var timer = Stopwatch.StartNew();
        bool acquired = false, sent = false;
        WorkerResponse response;
        CancellationTokenSource? call = null;
        try
        {
            using var queueTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            queueTimeout.CancelAfter(TimeSpan.FromSeconds(45));
            await gate.WaitAsync(queueTimeout.Token);
            acquired = true;
            call = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            call.CancelAfter(timeoutMs);
            Process worker;
            lock (processLock)
            {
                active = call;
                worker = EnsureStarted();
            }
            var request = new WorkerRequest(id, operation, Wire.Element(arguments));
            call.Token.ThrowIfCancellationRequested();
            // A failed pipe write can be partial; after this point a mutating request has an uncertain outcome.
            sent = true;
            await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, Wire.Json).AsMemory(), call.Token);
            await worker.StandardInput.FlushAsync(call.Token);
            string? line = await worker.StandardOutput.ReadLineAsync(call.Token);
            if (line is null) throw new IOException("Desktop worker exited before returning a result.");
            response = JsonSerializer.Deserialize<WorkerResponse>(line, Wire.Json) ?? throw new IOException("Empty worker response.");
            if (response.Id != id) throw new IOException("Mismatched worker response ID.");
        }
        catch (OperationCanceledException)
        {
            if (acquired) KillWorker();
            response = new(id, Error: new(acquired ? "cancelled_or_timeout" : "queue_cancelled_or_timeout",
                acquired ? "Desktop call stopped; all previous references are invalid. Re-list and inspect before continuing. Cancellation does not undo delivered actions."
                    : "Call ended while waiting for the desktop; no request was sent.",
                sent && operation == "perform_action" ? "unknown" : "not_executed"));
        }
        catch (DesktopException ex) { response = new(id, Error: ex.Error); }
        catch (Exception ex)
        {
            if (acquired) KillWorker();
            response = new(id, Error: new("worker_unavailable", ex.Message,
                sent && operation == "perform_action" ? "unknown" : "not_executed"));
        }
        finally
        {
            if (acquired)
            {
                lock (processLock) { if (ReferenceEquals(active, call)) active = null; }
                call?.Dispose();
                gate.Release();
            }
        }
        try
        {
            var args = Wire.Element(arguments);
            audit?.Invoke(new { timestamp = DateTimeOffset.UtcNow, requestId = id, operation,
                windowRef = args.TryGetProperty("windowRef", out var wr) ? wr.GetString() : null,
                action = args.TryGetProperty("action", out var action) ? action.GetString() : null,
                elapsedMs = timer.ElapsedMilliseconds, error = response.Error,
                result = response.Data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("execution", out var execution) ? execution.GetString() : null });
        }
        catch (Exception ex) { Console.Error.WriteLine($"Log write failed: {ex.Message}"); }
        return response;
    }

    public object Stop()
    {
        lock (processLock)
        {
            bool wasActive = active is not null;
            active?.Cancel();
            KillWorker();
            return new { stopped = true, wasActive, referencesInvalidated = true,
                note = "Delivered actions are not rolled back. Use list_windows before further operations." };
        }
    }

    private Process EnsureStarted()
    {
        if (options.RequireWindows && !OperatingSystem.IsWindows())
            throw new DesktopException("unsupported_platform", "The desktop worker requires an interactive Windows session. MCP protocol discovery is available on this OS, but desktop actions are not.");
        if (process is { HasExited: false }) return process;
        process?.Dispose();
        var info = new ProcessStartInfo(options.FileName)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in options.Arguments) info.ArgumentList.Add(argument);
        process = Process.Start(info) ?? throw new IOException("Could not start the Windows worker.");
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine($"[desktop-worker] {e.Data}"); };
        process.BeginErrorReadLine();
        return process;
    }

    private void KillWorker()
    {
        lock (processLock)
        {
            try { if (process is { HasExited: false }) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            process?.Dispose();
            process = null;
        }
    }

    public void Dispose() { Stop(); }
}
