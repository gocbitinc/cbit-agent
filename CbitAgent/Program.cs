using CbitAgent;
using CbitAgent.Configuration;
using CbitAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

// Set up Serilog with file + console + event log sinks at different levels
var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
Directory.CreateDirectory(logDir);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.File(
        path: Path.Combine(logDir, "agent.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        fileSizeLimitBytes: 10_000_000,
        flushToDiskInterval: TimeSpan.FromSeconds(1),
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Warning,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .WriteTo.EventLog("CBIT RMM Agent", manageEventSource: false,
        restrictedToMinimumLevel: LogEventLevel.Error)
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    // Configure as Windows Service
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "CBIT RMM Agent";
    });

    // Replace default logging with Serilog
    builder.Logging.ClearProviders();
    builder.Services.AddSerilog();

    // Register services
    builder.Services.AddSingleton<ConfigManager>();
    builder.Services.AddSingleton<ApiClient>();
    builder.Services.AddSingleton<SystemInfoCollector>();
    builder.Services.AddSingleton<NetworkInfoCollector>();
    builder.Services.AddSingleton<DiskInfoCollector>();
    builder.Services.AddSingleton<InstalledAppsCollector>();
    builder.Services.AddSingleton<PatchInfoCollector>();
    builder.Services.AddSingleton<ScreenConnectDetector>();
    builder.Services.AddSingleton<AgentUpdater>();
    builder.Services.AddSingleton<WindowsUpdateExecutor>();
    builder.Services.AddSingleton<WebSocketTerminalClient>();
    builder.Services.AddSingleton<ScriptExecutor>();
    builder.Services.AddSingleton<ServiceMonitor>();

    // Register the worker
    builder.Services.AddHostedService<Worker>();

    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "CBIT RMM Agent terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
