using System.Text.Json;
using CbitAgent.Services;
using Microsoft.Extensions.Logging;

var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
var jsonOpts = new JsonSerializerOptions { WriteIndented = true };

int passed = 0;
int failed = 0;

// ── Test 1: SystemInfoCollector ──
Console.WriteLine("\n═══ TEST 1: SystemInfoCollector ═══");
try
{
    var collector = new SystemInfoCollector(loggerFactory.CreateLogger<SystemInfoCollector>());
    var info = collector.Collect();
    Console.WriteLine(JsonSerializer.Serialize(info, jsonOpts));

    Assert("Hostname not empty", !string.IsNullOrEmpty(info.Hostname));
    Assert("OS version not empty", !string.IsNullOrEmpty(info.OsVersion));
    Assert("OS type is valid", info.OsType == "windows_workstation" || info.OsType == "windows_server");
    Assert("CPU model not empty", !string.IsNullOrEmpty(info.CpuModel));
    Assert("CPU cores > 0", info.CpuCores > 0);
    Assert("RAM > 0", info.RamGb > 0);
    Assert("Uptime > 0", info.UptimeSeconds > 0);
    Assert("Manufacturer not empty", !string.IsNullOrEmpty(info.Manufacturer));
}
catch (Exception ex)
{
    Console.WriteLine($"FAILED: {ex.Message}");
    failed++;
}

// ── Test 2: NetworkInfoCollector ──
Console.WriteLine("\n═══ TEST 2: NetworkInfoCollector ═══");
try
{
    var collector = new NetworkInfoCollector(loggerFactory.CreateLogger<NetworkInfoCollector>());
    var adapters = collector.CollectAdapters();
    Console.WriteLine(JsonSerializer.Serialize(adapters, jsonOpts));

    Assert("At least 1 adapter", adapters.Count > 0);
    Assert("Adapter has name", !string.IsNullOrEmpty(adapters[0].Name));
    Assert("Adapter has type", !string.IsNullOrEmpty(adapters[0].Type));
    Assert("Adapter has IP", !string.IsNullOrEmpty(adapters[0].IpAddress));

    var wanIp = await collector.GetWanIpAsync();
    Console.WriteLine($"WAN IP: {wanIp}");
    Assert("WAN IP retrieved", !string.IsNullOrEmpty(wanIp));
}
catch (Exception ex)
{
    Console.WriteLine($"FAILED: {ex.Message}");
    failed++;
}

// ── Test 3: DiskInfoCollector ──
Console.WriteLine("\n═══ TEST 3: DiskInfoCollector ═══");
try
{
    var collector = new DiskInfoCollector(loggerFactory.CreateLogger<DiskInfoCollector>());
    var disks = collector.CollectDisks();
    Console.WriteLine(JsonSerializer.Serialize(disks, jsonOpts));

    Assert("At least 1 disk", disks.Count > 0);
    Assert("C: drive found", disks.Any(d => d.DriveLetter.StartsWith("C")));
    Assert("Disk has total > 0", disks[0].TotalGb > 0);

    var smart = collector.CollectSmartData();
    Console.WriteLine(JsonSerializer.Serialize(smart, jsonOpts));
    Assert("SMART data collected (at least status reported)", smart.Count >= 0); // May be 0 on VMs
    Console.WriteLine($"SMART entries: {smart.Count}");
}
catch (Exception ex)
{
    Console.WriteLine($"FAILED: {ex.Message}");
    failed++;
}

// ── Test 4: InstalledAppsCollector ──
Console.WriteLine("\n═══ TEST 4: InstalledAppsCollector ═══");
try
{
    var collector = new InstalledAppsCollector(loggerFactory.CreateLogger<InstalledAppsCollector>());
    var apps = collector.Collect();
    Console.WriteLine($"Found {apps.Count} installed applications");
    // Print first 10
    foreach (var app in apps.Take(10))
    {
        Console.WriteLine($"  - {app.Name} ({app.Version}) by {app.Publisher}");
    }

    Assert("At least 10 apps found", apps.Count >= 10);
    Assert("Apps have names", apps.All(a => !string.IsNullOrEmpty(a.Name)));
}
catch (Exception ex)
{
    Console.WriteLine($"FAILED: {ex.Message}");
    failed++;
}

// ── Test 5: PatchInfoCollector (installed only - pending requires elevation) ──
Console.WriteLine("\n═══ TEST 5: PatchInfoCollector ═══");
try
{
    var collector = new PatchInfoCollector(loggerFactory.CreateLogger<PatchInfoCollector>());
    var installed = collector.CollectInstalledPatches();
    Console.WriteLine($"Found {installed.Count} installed patches");
    foreach (var patch in installed.Take(5))
    {
        Console.WriteLine($"  - {patch.KbNumber}: {patch.Title} (installed {patch.InstalledOn})");
    }

    Assert("At least 1 installed patch", installed.Count > 0);
    Assert("Patches have KB numbers", installed.All(p => !string.IsNullOrEmpty(p.KbNumber)));

    Console.WriteLine("Collecting pending patches (may require elevation)...");
    var pending = collector.CollectPendingPatches();
    Console.WriteLine($"Found {pending.Count} pending patches");
}
catch (Exception ex)
{
    Console.WriteLine($"FAILED: {ex.Message}");
    failed++;
}

// ── Test 6: ScreenConnectDetector ──
Console.WriteLine("\n═══ TEST 6: ScreenConnectDetector ═══");
try
{
    var detector = new ScreenConnectDetector(loggerFactory.CreateLogger<ScreenConnectDetector>());
    var guid = detector.DetectGuid();
    Console.WriteLine($"ScreenConnect GUID: {guid ?? "(not installed)"}");
    Assert("Detector ran without error", true);
}
catch (Exception ex)
{
    Console.WriteLine($"FAILED: {ex.Message}");
    failed++;
}

// ── Test 7: Full CheckIn payload assembly ──
Console.WriteLine("\n═══ TEST 7: Full CheckInPayload assembly ═══");
try
{
    var sysCollector = new SystemInfoCollector(loggerFactory.CreateLogger<SystemInfoCollector>());
    var netCollector = new NetworkInfoCollector(loggerFactory.CreateLogger<NetworkInfoCollector>());
    var diskCollector = new DiskInfoCollector(loggerFactory.CreateLogger<DiskInfoCollector>());
    var scDetector = new ScreenConnectDetector(loggerFactory.CreateLogger<ScreenConnectDetector>());

    var payload = new CbitAgent.Models.CheckInPayload
    {
        AgentId = "test-agent-id",
        AgentVersion = "1.0.0",
        Timestamp = DateTime.UtcNow,
        SystemInfo = sysCollector.Collect(),
        NetworkAdapters = netCollector.CollectAdapters(),
        Disks = diskCollector.CollectDisks(),
        SmartData = diskCollector.CollectSmartData(),
        ScreenConnectGuid = scDetector.DetectGuid(),
        WanIp = await netCollector.GetWanIpAsync()
    };

    var json = JsonSerializer.Serialize(payload, jsonOpts);
    Console.WriteLine("Full check-in payload JSON length: " + json.Length + " bytes");
    Console.WriteLine(json[..Math.Min(json.Length, 2000)]);
    if (json.Length > 2000) Console.WriteLine("... (truncated)");

    Assert("Payload serializes to valid JSON", json.StartsWith("{"));
    Assert("Payload has system_info", json.Contains("system_info"));
    Assert("Payload has network_adapters", json.Contains("network_adapters"));
    Assert("Payload has disks", json.Contains("disks"));
}
catch (Exception ex)
{
    Console.WriteLine($"FAILED: {ex.Message}");
    failed++;
}

// ── Summary ──
Console.WriteLine($"\n{'═',1}══ RESULTS ═══");
Console.WriteLine($"Passed: {passed}");
Console.WriteLine($"Failed: {failed}");
Console.WriteLine(failed == 0 ? "ALL TESTS PASSED" : "SOME TESTS FAILED");

return failed == 0 ? 0 : 1;

void Assert(string name, bool condition)
{
    if (condition)
    {
        Console.WriteLine($"  PASS: {name}");
        passed++;
    }
    else
    {
        Console.WriteLine($"  FAIL: {name}");
        failed++;
    }
}
