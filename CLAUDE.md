# CBIT RMM Agent — Project Context

## What This Is
C# Windows Service agent for the CBIT MSP Platform "Jarvis" (https://axis.gocbit.com). Part of a 10-phase internal MSP platform replacing SyncroMSP. This repo contains the Windows agent only — the server (Node.js/PostgreSQL/React) runs on a separate Linux VM.

## Current State
- Agent registers with server, checks in every 5 minutes, reports system info, network, disks, SMART, installed apps, patches, ScreenConnect GUID
- WebSocket terminal: echo fix applied (agent echoes input back, raw byte stream reading, cmd.exe /Q)
- WebSocket handles scan_updates, install_updates, install_kb, and reboot commands
- Windows Update executor: scan, download, install KBs, policy-based filtering, reboot detection
- HTTP check-in commands: install_kb (ad-hoc) and run_updates (policy-based) fully wired
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
- Auth: agent_token JWT in Authorization header for HTTP, query param for WebSocket

## Solution Structure
- CbitAgent/ — main agent service
- CbitAgent.Tray/ — system tray support app
- CbitAgent.Tests/ — test harness for collectors
- CbitAgent.Installer/ — WiX v6 MSI installer

## Key Files — Agent Service (CbitAgent/)
- Program.cs — DI setup, Windows Service config
- Worker.cs — main loop (registration, check-in, WebSocket, WU commands)
- Services/ApiClient.cs — HTTP client with retry, update job result reporting
- Services/SystemInfoCollector.cs — WMI queries
- Services/NetworkInfoCollector.cs — adapters, WiFi, WAN IP
- Services/DiskInfoCollector.cs — drives, SMART
- Services/InstalledAppsCollector.cs — registry query
- Services/PatchInfoCollector.cs — WMI + WUApiLib
- Services/ScreenConnectDetector.cs — service name parse
- Services/AgentUpdater.cs — download, hash verify, rollback
- Services/WebSocketTerminalClient.cs — terminal relay + WU command routing + install_kb + reboot
- Services/TerminalSession.cs — cmd/powershell process with raw byte I/O
- Services/WindowsUpdateExecutor.cs — WUApiLib COM: scan, install, policy filter, reboot check
- Models/TerminalMessages.cs — WsMessage/WsOutMessage (terminal + WU fields)
- Models/UpdateJobModels.cs — UpdateJobResult, UpdateProgress

## Key Files — Tray App (CbitAgent.Tray/)
- Program.cs — single-instance mutex, ApplicationContext (no visible form)
- TrayApplicationContext.cs — NotifyIcon, context menu, left-click computer name popup (300×130, 14pt bold, centered, above taskbar)
- SupportRequestForm.cs — email, description, screenshot capture, multipart POST
- ScreenshotCapture.cs — CopyFromScreen, thumbnail, PNG export
- TrayApiClient.cs — reads config from C:\Program Files\CBIT\Agent\config.json

## Key Files — Installer (CbitAgent.Installer/)
- Package.wxs — MSI package definition, directory structure, feature, custom actions (launch tray post-install, kill processes pre-uninstall)
- Components.wxs — agent exe + service, tray exe + Run key, cleanup on uninstall
- CbitAgent.Installer.wixproj — WiX v6 SDK, PublishDir + PublishTrayDir variables

## WebSocket Message Types
- terminal_start/stop/input/resize, terminal_output/started/error — remote terminal
- scan_updates → scan_result — Windows Update scan
- install_updates → update_progress → update_result — Windows Update batch install
- install_kb → patch_status (downloading/installing/completed/failed) — single KB install with job_id + job_asset_id, triggers fresh patch report on completion
- reboot → reboot_status (rebooting) — executes shutdown /r /t 10 /f
- ping/pong — keepalive

## MSI Behavior
- Installs to C:\Program Files\CBIT\Agent\
- Registers and starts CbitRmmAgent Windows Service (auto-start, LocalSystem)
- Adds HKLM Run key for CbitAgent.Tray.exe (auto-start for all users)
- Post-install custom action launches CbitAgent.Tray.exe as logged-in user (no reboot needed)
- On uninstall: early custom actions (before InstallValidate) force-kill CbitAgent.Tray.exe and CbitAgent.exe via taskkill, stop/delete service via sc.exe — prevents "files in use" prompt and hanging
- Cleanup removes all files including config.json, Agent and CBIT directories

## Build Commands
- Debug build: dotnet build
- Publish agent: dotnet publish CbitAgent/CbitAgent.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish
- Publish tray: dotnet publish CbitAgent.Tray/CbitAgent.Tray.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish-tray
- Build MSI: dotnet build CbitAgent.Installer/CbitAgent.Installer.wixproj -c Release
- Output: CbitAgent.Installer/bin/Release/CbitAgent.Installer.msi

## Known Issues
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
