using CbitAgent;
using CbitAgent.Configuration;
using CbitAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

// Global unhandled exception handler — write to fatal.log directly since Serilog may be unavailable
AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
{
    var ex = e.ExceptionObject as Exception;
    try
    {
        var fatalLogDir = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(fatalLogDir);
        var logPath = Path.Combine(fatalLogDir, "fatal.log");
        File.AppendAllText(logPath,
            $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} FATAL: {ex?.ToString()}\n");
    }
    catch { }
};

TaskScheduler.UnobservedTaskException += (sender, e) =>
{
    Log.Error(e.Exception, "Unobserved task exception");
    e.SetObserved();
};

// Set up Serilog with file + console + event log sinks at different levels
var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
Directory.CreateDirectory(logDir);

// Restrict log directory ACL to SYSTEM and Administrators
try
{
    var logDirInfo = new DirectoryInfo(logDir);
    var security = logDirInfo.GetAccessControl();
    security.SetAccessRuleProtection(true, false);
    security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
        "SYSTEM", System.Security.AccessControl.FileSystemRights.FullControl,
        System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
        System.Security.AccessControl.PropagationFlags.None,
        System.Security.AccessControl.AccessControlType.Allow));
    security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
        "BUILTIN\\Administrators", System.Security.AccessControl.FileSystemRights.FullControl,
        System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
        System.Security.AccessControl.PropagationFlags.None,
        System.Security.AccessControl.AccessControlType.Allow));
    logDirInfo.SetAccessControl(security);
}
catch (Exception ex)
{
    // Serilog not yet configured — write to fatal.log so ACL failures aren't silently lost
    try
    {
        File.AppendAllText(Path.Combine(logDir, "fatal.log"),
            $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} WARNING: Failed to set logs directory ACL: {ex.Message}\n");
    }
    catch { }
}

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
