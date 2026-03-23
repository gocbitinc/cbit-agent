# CBIT Agent — Progress

## 2026-03-22: CMD Terminal Removed — PowerShell Only

**Decision:** Removed CMD terminal support entirely. CMD's stdout buffering with redirected I/O (no real console/ConPTY) caused silent output — the UTF-8 encoding fix didn't resolve it. ConPTY would solve it but adds significant complexity. PowerShell covers all real-world RMM use cases.

**Changes:**
- `TerminalSession.cs` — Removed `shellType` parameter, CMD code path, `chcp 65001`, `PYTHONIOENCODING` env var. Always launches `powershell.exe -NoLogo -NoProfile -NonInteractive -Command -`
- `WebSocketTerminalClient.cs` — `HandleTerminalStart` ignores `shell_type` from server message, always spawns PowerShell
- `WsMessage.ShellType` field left in model (server may still send it) — agent simply ignores it

**Supersedes:** CMD Terminal UTF-8 Encoding Fix (same date)

- `TerminalSession.cs` — Set `StandardOutputEncoding = Encoding.UTF8` and `StandardErrorEncoding = Encoding.UTF8` for both CMD and PowerShell
- CMD sessions now send `chcp 65001 > nul` immediately after process start to switch the console code page to UTF-8 before any user input
- CMD sessions set `PYTHONIOENCODING=utf-8` environment variable for Python scripts run inside CMD
- PowerShell already handles UTF-8 natively but now has explicit encoding set for consistency

## 2026-03-22: WebSocket Auth Message Fix

**Fix applied** — WebSocket auth message now sends `agent_token` and `agent_id` in the message body. Previously the auth message sent only `type: "auth"` with `agent_id` in the generic `Data` field — server couldn't authenticate.

- `WebSocketTerminalClient.cs:131-148` — Auth message changed from `WsOutMessage { Type = "auth", Data = config.AgentId }` to raw JSON `{ type: "auth", agent_token: "...", agent_id: "..." }` sent via `_sendLock`-protected `SendAsync`
- WebSocket URL confirmed clean: `{wsUrl}/api/agent/ws` with no `?token=` query parameter (line 119)
- Token is sent only in the encrypted WebSocket message body, never in the URL

## 2026-03-22: Logs Directory ACL Verification

**Verified correct** — logs directory ACL was already set correctly in Program.cs:
- `NT AUTHORITY\SYSTEM` — Full Control (with ContainerInherit + ObjectInherit)
- `BUILTIN\Administrators` — Full Control (with ContainerInherit + ObjectInherit)
- `BUILTIN\Users` — No access (inheritance disabled, no Users entry)
- Log files (e.g., `agent20260322.log`) correctly inherit from parent directory

**Fix applied** — replaced empty `catch { }` block in Program.cs ACL code with error logging to `fatal.log`. Previously, if the ACL setting failed on a machine it would fail silently with no record. Now writes a warning to `logs/fatal.log` so failures are visible.

Verified on live machine:
```
IdentityReference      FileSystemRights AccessControlType
NT AUTHORITY\SYSTEM         FullControl             Allow
BUILTIN\Administrators      FullControl             Allow
```

config.json ACL also verified: SYSTEM + Administrators FullControl, no Users entry.

## 2026-03-22: HMAC-SHA256 Script Signature Verification

Implemented server-signed script verification to prevent tampering:

1. **AgentConfig.cs** — Added `script_signing_secret` field to config.json schema
2. **ScriptModels.cs** — Added `script_signature` field to `PendingScript` model
3. **CheckInResponse.cs** — Added `script_signing_secret` to both `CheckInResponse` and `RegisterResponse`
4. **ConfigManager.cs** — `UpdateRegistration` accepts signing secret; added `UpdateScriptSigningSecret` for check-in refresh
5. **Worker.cs** — Registration stores signing secret; every check-in refreshes it from server response
6. **ScriptExecutor.cs** — `VerifyScriptSignature` using HMAC-SHA256 with `CryptographicOperations.FixedTimeEquals` (timing-attack safe). Rejects ALL scripts if secret is missing, signature is missing, or signature doesn't match. Reports error result back to server on rejection.

Algorithm: HMAC-SHA256 over raw `script_content` (UTF-8), compared as lowercase hex. Secret delivered at registration and refreshed every check-in. Never logged.

## 2026-03-22: Security Hardening (7 Fixes)

All fixes from the security audit applied in one pass:

1. **CRITICAL: TLS bypass removed** (ApiClient.cs, ScriptExecutor.cs) — `ServerCertificateCustomValidationCallback` and all bypass code removed entirely. No TLS bypass exists in the binary. Plain `new HttpClient()` used. For local dev with self-signed certs, add the cert to the Windows trusted root certificate store.

2. **HIGH: config.json ACL locked down** (ConfigManager.cs) — File permissions restricted to SYSTEM + Administrators after every write. Also enforced on startup for existing deployments.

3. **HIGH: Update hash verification mandatory** (AgentUpdater.cs) — Updates without a server-provided SHA256 hash are now rejected. Previously the hash check was optional.

4. **MEDIUM: JWT token removed from WebSocket URL** (WebSocketTerminalClient.cs) — Token no longer sent in query string. Auth is via the existing auth message after connection (line 131-135). **NOTE: CC Linux server may need to update the WebSocket handler to not require the URL token.**

5. **MEDIUM: Sensitive logging reduced** (WebSocketTerminalClient.cs, SystemInfoCollector.cs, ScreenConnectDetector.cs) — Full WS message content, serial numbers, ScreenConnect GUIDs, usernames, and domain names moved from Information to Debug level.

6. **MEDIUM: Registry key ACL restricted** (Worker.cs) — `HKLM\SOFTWARE\CBIT\Agent` restricted to SYSTEM + Administrators on every startup.

7. **401 re-registration handling** (Worker.cs) — When server returns 401 Unauthorized during check-in, agent clears credentials and re-registers with customer key. Prepares for short-lived JWT token rotation.

## 2026-03-22: Production Readiness (10 Sections)

### 1. Service Recovery & Reliability
**Fix applied** — Added `util:ServiceConfig` to Components.wxs with automatic restart on all three failure actions (30s delay, 1-day reset). Required adding WixToolset.Util.wixext to installer project.

### 2. Memory & Resource Management
**Fixes applied:**
- ScriptExecutor.cs: Replaced per-request `new HttpClient()` with static `SharedHttpClient` — prevents socket exhaustion
- NetworkInfoCollector.cs: Replaced per-request `new HttpClient()` with static `WanIpClient` — same fix
- All other services use singletons via DI, no unbounded collections found
- CancellationToken is passed through all async chains
- Task.Delay loops all respect cancellation

### 3. Unhandled Exception Protection
**Fix applied** — Added to Program.cs:
- `AppDomain.CurrentDomain.UnhandledException` handler: writes to `logs/fatal.log` directly (bypasses Serilog in case it's unavailable)
- `TaskScheduler.UnobservedTaskException` handler: logs error and calls `SetObserved()`

### 4. Graceful Shutdown
**No issues found.** `BackgroundService.StopAsync` cancels the `stoppingToken` which:
- Cancels all in-flight HTTP requests (CancellationToken passed through)
- WebSocket `RunAsync` catches `OperationCanceledException` and disposes sessions
- `Log.CloseAndFlush()` already in Program.cs `finally` block

### 5. Log File Management
**Fix applied:**
- Log directory ACL restricted to SYSTEM + Administrators on startup (with inheritance for child files)
- Serilog was already configured correctly: daily rolling, 7-day retention, 10MB max per file
- Disk space: Serilog's `fileSizeLimitBytes` prevents runaway growth; 7-day retention caps total

### 6. Network Resilience
**Fixes applied:**
- ApiClient: 30s timeout and exponential backoff already correct
- Added check-in overlap guard (`_checkInRunning` flag) — skips cycle if previous is still running
- DNS/network failures handled gracefully by existing catch blocks in check-in loop

### 7. Input Validation
**Fixes applied:**
- ScriptExecutor: timeout capped at 3600s (1 hour max); empty script content rejected; execution_id validated against `Path.GetInvalidFileNameChars()` and `..` to prevent path traversal
- ServiceMonitorConfig: service names rejected if they contain `\`, `/`, `"`, `'`, `<`, `>`, `|`, `&`, `;`; max 50 services and 50 events enforced

### 8. Startup Validation
**Fixes applied:**
- Agent logs version, OS, and .NET runtime at startup
- `server_url` validated as HTTPS — agent refuses to connect over HTTP
- Registration retry with exponential backoff (up to 10 attempts, max 300s delay) instead of immediate exit on failure

### 9. Sensitive Data Protection
**No issues found.** Grep of all Log calls shows:
- `agent_token` is never logged (not even at Debug level)
- `customer_key` value is never logged — only contextual messages like "from registry"
- No passwords, secrets, or credentials logged anywhere

### 10. Code Quality & Stability
**Fix applied:**
- WebSocketTerminalClient: empty `catch { }` on close frame now logs at Debug level
- All other catch blocks already log the exception or are justified COM cleanup (e.g., `Marshal.ReleaseComObject` in finally blocks)
- No silently swallowed exceptions found

### Build Output
- MSI: `CbitAgent.Installer/bin/Release/CbitAgent.Installer.msi` (76 MB)
- Bundle: `CbitAgent.Bundle/bin/Release/CbitAgent.Bundle.exe` (100 MB)
