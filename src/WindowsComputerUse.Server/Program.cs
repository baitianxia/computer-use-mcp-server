using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WindowsComputerUse.Core;
using WindowsComputerUse.Server;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
var workerPath = Environment.GetEnvironmentVariable("WINDOWS_CU_WORKER_PATH")
    ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "worker", "WindowsComputerUse.Worker.exe"));
var workerOptions = workerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
    ? new WorkerOptions("dotnet", [workerPath]) : new WorkerOptions(workerPath, []);
string logDir = Environment.GetEnvironmentVariable("WINDOWS_CU_LOG_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsComputerUse", "logs");
object logLock = new();
builder.Services.AddSingleton(new WorkerClient(workerOptions, entry =>
{
    lock (logLock)
    {
        Directory.CreateDirectory(logDir);
        File.AppendAllText(Path.Combine(logDir, $"actions-{DateTime.UtcNow:yyyy-MM-dd}.jsonl"), JsonSerializer.Serialize(entry, Wire.Json) + Environment.NewLine, new UTF8Encoding(false));
    }
}));
string guidanceDirectory = Environment.GetEnvironmentVariable("WINDOWS_CU_GUIDANCE_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsComputerUse", "guidance");
string claudeDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
builder.Services.AddSingleton(new GuidanceOptions(guidanceDirectory, Path.Combine(claudeDirectory, "skills")));
builder.Services.AddSingleton<GuidanceService>();
builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<DesktopTools>().WithTools<GuidanceTools>();
// Restore the handoff barrier before any perform_action can arrive after a restart.
using var host = builder.Build();
_ = host.Services.GetRequiredService<GuidanceService>();
await host.RunAsync();
