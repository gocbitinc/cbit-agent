using CbitAgent;
using CbitAgent.Configuration;
using CbitAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Configure as Windows Service
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "CBIT RMM Agent";
});

// Configure logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddEventLog(settings =>
{
    settings.SourceName = "CBIT RMM Agent";
    settings.LogName = "Application";
});
builder.Logging.SetMinimumLevel(LogLevel.Information);

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

// Register the worker
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
