# CBIT RMM Agent — Project Context

## What This Is
C# Windows Service agent for the CBIT MSP Platform "Jarvis" (https://axis.gocbit.com). Part of a 10-phase internal MSP platform replacing SyncroMSP. This repo contains the Windows agent only — the server (Node.js/PostgreSQL/React) runs on a separate Linux VM.

## Current State
- Agent registers with server, checks in every 5 minutes, reports system info, network, disks, SMART, installed apps, patches, ScreenConnect GUID, CPU/RAM usage, pending reboot, Defender status, BitLocker status, local admins
- WebSocket terminal: PowerShell only (CMD removed — stdout buffering unsolvable without ConPTY), agent echoes input back, raw byte stream reading
- WebSocket handles scan_updates, install_updates, install_kb, and reboot commands
- Windows Update executor: scan, download, install KBs, policy-based filtering, reboot detection
- HTTP check-in commands: install_kb (ad-hoc) and run_updates (policy-based) fully wired
- PowerShell scripting engine: heartbeat-delivered script execution with file download, variable injection, timeout enforcement, result reporting
- System tray app (CbitAgent.Tray): support request form with email field, screenshot capture, computer name popup on left-click
- MSI installer includes both agent service and tray app, auto-start via Run registry key, full cleanup on uninstall
- Agent runs as Windows Service "CbitRmmAgent" under LocalSystem
- Test machine: Dell OptiPlex 3070, Windows 11 Pro

## Architecture
- .NET 8 Worker Service, self-contained single-file publish for win-x64 (~68 MB)
- .NET 8 WinForms tray app, self-contained single-file publish for win-x64 (~155 MB)
- config.json next to exe: server_url, customer_key, agent_id, agent_token
- Server API base: https://axis.gocbit.com/api/agent/
- WebSocket: wss://axis.gocbit.com/api/agent/ws
- Auth: agent_token JWT in Authorization header for HTTP; WebSocket auth sends `{type: "auth", agent_token, agent_id}` in message body (no token in URL)

## Repository
- GitHub: https://github.com/gocbitinc/cbit-agent.git
- Branch: main
- Git identity: CBIT Inc. <support@gocbit.com>

## Solution Structure
- CbitAgent/ — main agent service
- CbitAgent.Tray/ — system tray support app
- CbitAgent.Tests/ — test harness for collectors
- CbitAgent.Installer/ — WiX v6 MSI installer
- CbitAgent.Bundle/ — WiX v6 Burn bootstrapper (MSI + VC++ Redistributable)

## Key Files — Agent Service (CbitAgent/)
- Program.cs — DI setup, Windows Service config, Serilog logging (file + console + event log)
- Worker.cs — main loop (registration, check-in, WebSocket, WU commands)
- Services/ApiClient.cs — HTTP client with retry, update job result reporting
- Services/SystemInfoCollector.cs — WMI queries
- Services/NetworkInfoCollector.cs — all adapters (physical/virtual/VPN/tunnel/loopback), all IPs per adapter, WiFi, WAN IP
- Services/DiskInfoCollector.cs — drives, SMART
- Services/InstalledAppsCollector.cs — registry query
- Services/PatchInfoCollector.cs — WMI + WUApiLib
- Services/ScreenConnectDetector.cs — service name parse
- Services/AgentUpdater.cs — download, hash verify, rollback
- Services/WebSocketTerminalClient.cs — terminal relay + WU command routing + install_kb + reboot
- Services/TerminalSession.cs — PowerShell process with raw byte I/O (CMD removed)
- Services/WindowsUpdateExecutor.cs — WUApiLib COM: scan, install, policy filter, reboot check
- Models/TerminalMessages.cs — WsMessage/WsOutMessage (terminal + WU fields)
- Services/ScriptExecutor.cs — PowerShell script execution: file download, variable injection, process spawn, timeout, result reporting
- Services/ServiceMonitor.cs — Windows service and event log monitoring with auto-restart and alerts
- Services/ServiceMonitorConfig.cs — INI config parser for service-monitor.ini
- Models/ScriptModels.cs — PendingScript, ScriptFile, PowerShellResult, ScriptResult
- Models/UpdateJobModels.cs — UpdateJobResult, UpdateProgress
- Models/AlertModels.cs — ServiceAlertPayload, EventAlertPayload

## Key Files — Tray App (CbitAgent.Tray/)
- Program.cs — single-instance mutex, ApplicationContext (no visible form)
- TrayApplicationContext.cs — NotifyIcon, context menu, left-click computer name popup (300×130, 14pt bold, centered, above taskbar)
- SupportRequestForm.cs — email, description, screenshot capture, multipart POST
- ScreenshotCapture.cs — CopyFromScreen, thumbnail, PNG export
- TrayApiClient.cs — reads config from C:\Program Files\CBIT\Agent\config.json

## Key Files — Installer (CbitAgent.Installer/)
- Package.wxs — MSI package definition, directory structure, feature, custom actions (launch tray post-install, kill processes pre-uninstall)
- Components.wxs — agent exe + service, tray exe + Run key, customer key registry, cleanup on uninstall
- CbitAgent.Installer.wixproj — WiX v6 SDK, AgentPublishDir + TrayPublishDir properties (avoid MSBuild reserved name PublishDir)
- vc_redist.x64.exe — VC++ 2015-2022 x64 Redistributable (downloaded, not in git)

## Key Files — Bundle (CbitAgent.Bundle/)
- Bundle.wxs — Burn bootstrapper: chains VC++ Redistributable + agent MSI, silent install, CUSTOMER_KEY passthrough
- CbitAgent.Bundle.wixproj — WiX v6 Bundle SDK with Bal + Util extensions

## WebSocket Message Types
- terminal_start/stop/input/resize, terminal_output/started/error — remote terminal
- scan_updates → scan_result — Windows Update scan
- install_updates → update_progress → update_result — Windows Update batch install
- install_kb → patch_status (downloading/installing/completed/failed) — single KB install with job_id + job_asset_id, triggers fresh patch report on completion
- reboot → reboot_status (rebooting) — executes shutdown /r /t 10 /f
- ping/pong — keepalive

## Customer Key Delivery (MSI Property Table)
- Server patches `CUSTOMER_KEY` in MSI Property table before download (Windows Installer COM API or msitools on Linux)
- MSI writes `[CUSTOMER_KEY]` to registry: `HKLM\SOFTWARE\CBIT\Agent\CustomerKey`
- Agent reads config.json → if no customer_key, reads registry → saves to config.json
- Placeholder value `CBIT_YOURKEY` is ignored (treated as empty)
- No binary patching — Property table is always uncompressed OLE structured storage
- Server-side patching: `UPDATE Property SET Value='real-key' WHERE Property='CUSTOMER_KEY'`
- Install with key: `msiexec /i CbitAgent.Installer.msi CUSTOMER_KEY=<key>`

## MSI Behavior
- Installs to C:\Program Files\CBIT\Agent\
- Registers and starts CbitRmmAgent Windows Service (auto-start, LocalSystem)
- Adds HKLM Run key for CbitAgent.Tray.exe (auto-start for all users)
- Writes CUSTOMER_KEY property to registry (HKLM\SOFTWARE\CBIT\Agent\CustomerKey)
- Post-install custom action launches CbitAgent.Tray.exe as logged-in user (no reboot needed)
- On uninstall: early custom actions (before InstallValidate) force-kill CbitAgent.Tray.exe and CbitAgent.exe via taskkill, stop/delete service via sc.exe — prevents "files in use" prompt and hanging
- Creates `logs\` subdirectory at install time for agent log files
- Cleanup removes all files including config.json, logs, Agent and CBIT directories
- Compression: high (embedded cab), MSI ~78 MB
- Bundle bootstrapper (CbitAgent.Bundle.exe ~100 MB) chains VC++ Redistributable + MSI
- Bundle detects existing VC++ via registry (HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64), skips if installed
- Bundle accepts CUSTOMER_KEY variable and passes to MSI
- GPO can deploy MSI directly (VC++ usually present on managed machines); bundle is for clean installs

## Build Commands
- Debug build: dotnet build
- Publish agent: dotnet publish CbitAgent/CbitAgent.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish
- Publish tray: dotnet publish CbitAgent.Tray/CbitAgent.Tray.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish-tray
- Build MSI: dotnet build CbitAgent.Installer/CbitAgent.Installer.wixproj -c Release
- Build Bundle: dotnet build CbitAgent.Bundle/CbitAgent.Bundle.wixproj -c Release (requires MSI built first)
- Download VC++ redist: Invoke-WebRequest -Uri "https://aka.ms/vs/17/release/vc_redist.x64.exe" -OutFile "CbitAgent.Installer\vc_redist.x64.exe"
- Output MSI: CbitAgent.Installer/bin/Release/CbitAgent.Installer.msi
- Output Bundle: CbitAgent.Bundle/bin/Release/CbitAgent.Bundle.exe
- Install MSI: msiexec /i CbitAgent.Installer.msi CUSTOMER_KEY=<key>
- Install Bundle: CbitAgent.Bundle.exe /quiet /norestart CUSTOMER_KEY=<key>

## Scripting Engine
- Server delivers PowerShell scripts via `pending_script` in heartbeat response
- One script at a time (`_scriptInProgress` guard), queued scripts re-sent next heartbeat
- **HMAC-SHA256 signature verification**: every script must include `script_signature` (lowercase hex HMAC-SHA256 of raw `script_content`). Agent verifies using `script_signing_secret` from config.json (delivered at registration, refreshed every check-in). Uses `CryptographicOperations.FixedTimeEquals` (timing-attack safe). Rejects ALL scripts if secret missing, signature missing, or mismatch.
- Variable injection: replaces `{{Key}}` in script content before execution
- Files downloaded to temp working directory (`axis-script-{executionId}`)
- PowerShell: `-NoProfile -NonInteractive -ExecutionPolicy Bypass -File`
- Timeout: `Kill(entireProcessTree: true)` after `timeout_seconds`
- Output capped at 256KB during capture
- Results reported to `POST /api/agent/scripts/{execution_id}/result`
- Status: success (exit 0), failed (non-zero), timeout, error (agent-level failure)
- Spec: `C:\dev\axis-scripting-windows.md`

## Service & Event Log Monitoring
- ServiceMonitor checks Windows services and event logs every check-in cycle
- Config: `service-monitor.ini` in agent install directory (re-read each cycle, no restart needed)
- Services: auto-restart via sc.exe (bypasses ServiceController permission issues), 120s delay before alert, recovery alerts
- Events: queries event logs via EventLogQuery/EventLogReader (no EventLogSession — that caused RPC errors)
- Security event log: MSI grants SeSecurityPrivilege to LocalSystem via secedit custom action
- Graceful fallback: UnauthorizedAccessException on event logs logged as warning, never crashes
- Alerts posted to `POST /api/agent/alerts` as JSON array
- Models: `Models/AlertModels.cs` (ServiceAlertPayload, EventAlertPayload)
- Config parser: `Services/ServiceMonitorConfig.cs` (INI format with [services] and [events] sections)
- Monitor: `Services/ServiceMonitor.cs` (persistent instance, survives across check-ins)
- Monitoring failures never affect normal check-in operation

## Logging
- Serilog with three sinks: File, Console, Windows Event Log
- File: `{install_dir}\logs\agent.log`, daily rolling, 7-day retention, 10MB max per file, Information+
- Console: Warning+ (reduced noise)
- Windows Event Log: Error+ only (source: "CBIT RMM Agent")
- Format: `2026-03-21 14:23:01 [INF] CbitAgent.Worker: Check-in successful`
- NuGet: Serilog.Extensions.Hosting, Serilog.Sinks.File, Serilog.Sinks.Console, Serilog.Sinks.EventLog

## Security Hardening (2026-03-22)
- TLS: Strict certificate validation, no bypass path in code. For local dev with self-signed certs, add the cert to the Windows trusted root certificate store.
- config.json ACL: Restricted to SYSTEM + Administrators on every write and on startup (disables inheritance).
- Registry ACL: `HKLM\SOFTWARE\CBIT\Agent` restricted to SYSTEM + Administrators on startup.
- Update integrity: SHA256 file hash is mandatory for all agent updates (rejects if server omits hash).
- WebSocket auth: Token sent in message body as `{type: "auth", agent_token, agent_id}`. No token in URL query string.
- Logging: Sensitive data (serial numbers, GUIDs, domains, usernames, full WS messages) moved to Debug level.
- 401 re-registration: Agent clears token and re-registers when server returns 401 Unauthorized.

## Production Readiness (2026-03-22)
- Service recovery: Windows service auto-restarts on failure (30s delay, 3 retries, 1-day reset)
- Startup: logs agent version, OS, .NET runtime; validates server_url is HTTPS; registration retries with backoff
- Unhandled exceptions: `AppDomain.UnhandledException` writes to `logs/fatal.log`; `TaskScheduler.UnobservedTaskException` logged and observed
- HttpClient: shared static instances in ScriptExecutor and NetworkInfoCollector (prevents socket exhaustion)
- Input validation: script timeout capped at 3600s, execution_id path traversal blocked, service names validated
- Log directory: ACL restricted to SYSTEM + Administrators Full Control with ContainerInherit + ObjectInherit (child log files inherit). ACL failure logs to `fatal.log` instead of silently swallowing.
- Check-in overlap guard: skips cycle if previous check-in is still running

## Known Issues
- WebSocket server must accept auth-by-message: `{type: "auth", agent_token, agent_id}` — agent no longer sends token in URL
- No MSI auto-update yet (agent_updater logic exists but untested)
- Support request endpoint /api/agent/support-request needs server-side implementation

## Remaining Work
- Phase 8: Alerts and notifications (server only)
- Phase 9: Dashboards and reporting (server only)
- Server-side: support-request endpoint, Windows Update job scheduling UI

## Contact
- CBIT Inc. — (509) 578-5424 — support@gocbit.com

## Reference Docs
- MASTER_PLAN.md — full platform architecture
- phases/PHASE_05_RMM_AGENT.md — agent spec (Steps 8-17)
- phases/PHASE_06_TERMINAL.md — WebSocket terminal spec (Step 4, 9)
