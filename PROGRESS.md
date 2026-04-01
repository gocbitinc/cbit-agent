# CBIT Agent — Progress

## 2026-03-31: Security Fixes — Batch 4 (12 Low-severity findings)

### L1 — TLS floor on HttpClient
`ApiClient.cs`: `HttpClient` is now constructed with an explicit `HttpClientHandler` that sets `SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13`. TLS 1.0 and 1.1 connections are rejected at the socket level. `ClientWebSocket` on .NET 8 / Windows inherits OS Schannel which also honours the system TLS floor; no additional override required.

### L2 — Remove third-party WAN IP collection
`NetworkInfoCollector.cs`: removed `GetWanIpAsync()`, `WanIpClient`, `_cachedWanIp`, `_wanIpCacheTime`, and `WanIpCacheLifetime`. Every 5-minute check-in was leaking the endpoint's public IP to `ipify.org`, `ifconfig.me`, and `icanhazip.com`. Server will now derive the WAN IP from the inbound HTTP request `X-Forwarded-For` / remote address. `Worker.cs` no longer calls `GetWanIpAsync()` or populates `WanIp`. `CheckInPayload.WanIp` marked `[Obsolete]` for documentation; field retained for backwards-compatible JSON serialisation (always null).

### L3 — XPath EventId injection prevention
`ServiceMonitorConfig.ParseEventLine()`: EventId is now validated with `int.TryParse(...) && value > 0` before being accepted. Invalid values (non-numeric, negative, zero) cause the event entry to be rejected and a warning logged. The integer string is then stored so it is safe for direct use in the XPath `EventID={entry.EventId}` query in `ServiceMonitor.cs`.

### L4 — Check-in interval clamped to [1, 60] minutes
`Worker.PerformCheckInAsync()`: server-supplied `CheckInIntervalMinutes` is clamped to `Math.Clamp(value, 1, 60)` before applying. A warning is logged when the server sends an out-of-range value. Prevents a compromised server from driving poll-storms (<1 min) or effectively disabling the agent (>60 min).

### L5 — ScreenConnect instance ID format validation
`Worker.PerformCheckInAsync()`: `screenconnect_instance_id` from the server is validated against `^[0-9a-fA-F]{16}$` before being forwarded to `ConfigManager.UpdateScreenConnectInstanceId()`. Invalid values are discarded with a warning; the current config is unchanged.

### L6 — SigningPublicKey validated at load time
`ConfigManager.Load()`: after loading `signing_public_key` from config.json or registry, the PEM is validated by calling `RSA.Create().ImportFromPem()`. If the import throws (malformed PEM, wrong key type, truncated data), the key is cleared and an error is logged — scripts will be rejected until a reinstall delivers a valid key. Prevents a "silent pass" scenario where a corrupt key causes all signatures to appear valid through a different code path.

### L7 — Explicit PSS salt length documentation
`ScriptExecutor.VerifyScriptSignature()`: added inline comment documenting that `.NET PSS` uses `salt length = hash length = 32 bytes for SHA-256` (PKCS#1 v2.2 recommended). This pins the intent so future maintainers cannot silently alter the expectation without updating both agent and server signing logic. `RSASignaturePadding.Pss` is the correct .NET 8 API; there is no `CreatePss` factory method in this framework version.

### L9 — volatile on cross-thread bool fields
`Worker.cs`: `_scriptInProgress` and `_checkInRunning` declared `volatile`. Both are set from `Task.Run` pool threads and read from the check-in loop. `volatile` ensures reads see the latest write without a lock and prevents compiler/JIT reordering.

### L10 — WaitForExitAsync after process Kill
`ScriptExecutor.RunPowerShellAsync()`: after `process.Kill(entireProcessTree: true)`, the code now awaits `process.WaitForExitAsync()` with a 5-second timeout via `.WaitAsync(TimeSpan.FromSeconds(5))`. Without this, the process tree could still hold open file handles on the `workDir` when the finally-block tries to delete it, causing `Directory.Delete` to silently fail and leaving temp files on disk.

### L11 — Sanitize exception messages before server transmission
`ScriptExecutor.cs`: added `SanitizeErrorMessage(string?)` helper. Replaces absolute Windows paths (e.g. `C:\Users\admin\AppData\...`) and UNC paths (`\\server\share`) with `[path]` via regex; truncates to 512 characters. Applied to `ex.Message` in the agent execution error `Stderr` field (the only place where raw exception text was transmitted to the server). Filename in helper-file-rejected errors is no longer echoed back.

### L12 — Screenshot capture deferred to user opt-in
`SupportRequestForm.cs`: removed `Bitmap? screenshot` constructor parameter. Checkbox now starts **unchecked** (was checked). Screenshot is **not** captured until the user checks "Include screenshot" — at that point a fresh capture happens in `OnScreenshotCheckChanged`. On uncheck, the captured bitmap is disposed immediately. `TrayApplicationContext.OnSupportRequest` no longer calls `CaptureFullScreen()` before the form opens. "Capture New Screenshot" button still works when checkbox is checked. Result: no screenshot of the user's screen is taken without explicit consent.

### Build
0 errors, 0 warnings (Release, agent + tray + MSI).

### Files Changed
| File | Changes |
|------|---------|
| `Services/ApiClient.cs` | L1: TLS 1.2/1.3 HttpClientHandler |
| `Services/NetworkInfoCollector.cs` | L2: removed WanIpClient + GetWanIpAsync |
| `Models/CheckInPayload.cs` | L2: WanIp marked [Obsolete] |
| `Worker.cs` | L2: removed GetWanIpAsync call; L4: clamp interval; L5: hex16 SC regex; L9: volatile bools |
| `Services/ServiceMonitorConfig.cs` | L3: int.TryParse EventId validation |
| `Configuration/ConfigManager.cs` | L6: RSA PEM validation at load |
| `Services/ScriptExecutor.cs` | L7: PSS salt comment; L10: WaitForExitAsync; L11: SanitizeErrorMessage |
| `CbitAgent.Tray/SupportRequestForm.cs` | L12: deferred screenshot, unchecked default, OnScreenshotCheckChanged |
| `CbitAgent.Tray/TrayApplicationContext.cs` | L12: removed pre-capture before form open |

---

## 2026-04-01: Terminal Hardening — Session Watchdog (M1 + L8)

### M1 — Kill orphaned sessions on disconnect
`TerminalSession` now tracks `LastActivityUtc` (updated on every stdout/stderr byte read and every `WriteInput` call). A background watchdog task started in `WebSocketTerminalClient.RunAsync` checks all active sessions every 60 seconds. If the WebSocket is not connected and a session's `LastActivityUtc` is older than 10 minutes, the session is disposed and removed from `_sessions`. The 10-minute window preserves sessions through brief reconnections without allowing indefinite orphaning.

### L8 — Maximum session duration watchdog
`TerminalSession` now tracks `StartedAtUtc` (set once at construction). The same watchdog disposes any session older than 4 hours (twice the server-side 2h token expiry) regardless of activity or connection state. A warning is logged before disposal.

### Implementation details
- Watchdog: `SessionWatchdogAsync(CancellationToken)` — loops on `Task.Delay(60s)`, cancels cleanly via `stoppingToken`
- Constants: `MaxSessionAge = 4h`, `InactivityDisconnectTimeout = 10min`, `WatchdogInterval = 60s`
- Thread safety: uses `ConcurrentDictionary.TryRemove` before `Dispose()` to prevent double-disposal
- `CleanupConnection()` is unchanged — sessions still persist across reconnections by design
- Watchdog task is awaited after the reconnect loop exits so service shutdown is clean
- `terminal_stop` message handling unchanged — explicit stop from server still takes priority

### Files Changed
| File | Change |
|------|--------|
| `Services/TerminalSession.cs` | Added `StartedAtUtc`, `LastActivityUtc`; update `LastActivityUtc` in `ReadStreamAsync` and `WriteInput` |
| `Services/WebSocketTerminalClient.cs` | Added 3 constants, `SessionWatchdogAsync()`, wired into `RunAsync` |

### Build
0 errors, 0 warnings (Release mode).

---

## 2026-03-31: Agent JWT Rotation on Every Check-in

Server now issues a fresh 24h JWT on every successful check-in. Old token is immediately invalidated server-side.

### What Changed

1. **`Models/CheckInResponse.cs`** — Added nullable `agent_token` (`string?`) field with `[JsonPropertyName("agent_token")]`. Present on every successful check-in response.

2. **`Worker.cs` — `PerformCheckInAsync()`** — Immediately after a successful check-in response is parsed, the rotated token is written to `config.AgentToken` and persisted via `ConfigManager.Save()` before any other response processing (interval update, ScreenConnect, commands, scripts). Missing token logs a warning and continues using existing token (grace period for server rollout).

3. **`Services/ApiClient.cs`** — Verified clean: `SetAuthHeaders()` reads `_configManager.Config.AgentToken` on every call — no caching. Rotated token is picked up immediately on the next request.

4. **401 re-registration flow** — Verified intact: `HttpStatusCode.Unauthorized` catch, `TryRefreshCustomerKey()`, and `ClearEnrollmentKey()` all untouched.

### Token Lifecycle
- Registration → server issues initial token → persisted to config.json
- Every check-in → server issues new 24h token → agent overwrites config.AgentToken + saves
- 401 → agent clears AgentId/AgentToken, attempts re-registration using customer_key from registry

### Files Changed
| File | Change |
|------|--------|
| `Models/CheckInResponse.cs` | Added `agent_token` (`string?`) |
| `Worker.cs` | Token rotation block after successful check-in |

### Build
0 errors, 0 warnings (Release mode).

---

## 2026-03-31: Security Fixes — Batch 1 (4 High findings from audit)

### H4 — WebSocket terminal token fallback removed (WebSocketTerminalClient.cs)
Removed the fallback to `config.AgentToken` when `RequestTerminalSessionTokenAsync()` returns null/empty. Agent now throws `InvalidOperationException` with a security-relevant error log, causing the exponential-backoff reconnect loop to retry later. The agent_token (long-lived JWT) will never be sent over the WebSocket channel.

### H1 — Helper file path traversal blocked (ScriptExecutor.cs)
Added `ValidateHelperFilename()` before every `Path.Combine`. Rules: filename must equal `Path.GetFileName()` of itself (rejects embedded `/` or `\`), must not be empty or start with `.`, must not contain `: * ? " < > |` or `..`. Rejection is logged as a security warning (including the raw server-supplied value) and reports an error result to the server.

### H2 — Helper file SHA-256 integrity check (ScriptExecutor.cs + Models/ScriptModels.cs)
Added `file_hash` (SHA-256 hex) field to `ScriptFile` model. Downloads now go to memory via `ReadAsByteArrayAsync()`, `SHA256.HashData()` is computed, compared against the signed payload's `file_hash` (case-insensitive), and the file is only written to disk after the hash matches. Missing hash → immediate error result. Hash mismatch → exception with security log. **Server-side requirement pending** — see MEMORY.md.

### H3 — Enrollment key cleared after registration (ConfigManager.cs + Worker.cs)
After successful registration, `ClearEnrollmentKey()` clears `customer_key` from in-memory config, re-saves config.json with empty value, and deletes `CustomerKey` from `HKLM\SOFTWARE\CBIT\Agent` registry. The enrollment secret is retained no longer than needed. On 401 re-registration, `TryRefreshCustomerKey()` attempts a registry re-read (succeeds if agent was recently reinstalled). If key is unavailable, logs a clear "reinstall required" error and lets `RegisterAsync` fail gracefully (no loop break — loop continues for operator visibility).

### Files Changed
| File | Change |
|------|--------|
| `Services/WebSocketTerminalClient.cs` | Removed AgentToken fallback; throw on missing session token |
| `Services/ScriptExecutor.cs` | `ValidateHelperFilename()`, `DownloadAndVerifyFileAsync()` with SHA-256 check; removed `DownloadFileAsync()` |
| `Models/ScriptModels.cs` | Added `file_hash` (`string?`) to `ScriptFile` |
| `Configuration/ConfigManager.cs` | Added `ClearEnrollmentKey()`, `TryRefreshCustomerKey()` |
| `Worker.cs` | Calls `ClearEnrollmentKey()` after registration; 401 handler calls `TryRefreshCustomerKey()` and `Save()` |

### Build
0 errors, 0 warnings (Release mode).

---

## 2026-03-30: Security Audit — Static Analysis (Report Only)

Full security audit of the Windows agent codebase completed. No code changes made.

**Report location:** `C:\Dev\cbit-agent\SECURITY_AUDIT_WINDOWS_AGENT.md`

**Summary:**
- 2 High, 6 Medium, 9 Low, 3 Info findings
- No Critical findings

**Top 3 Priority Fixes:**
1. **[HIGH] Helper file path traversal** — `file.Filename` used in `Path.Combine` without validation in `ScriptExecutor.cs`. A validly-signed script with a crafted filename can write files outside the temp working directory.
2. **[MEDIUM] No Authenticode signing** — Agent binary is not code-signed. Post-install binary replacement is undetectable by Windows.
3. **[MEDIUM] Orphaned terminal sessions** — Terminal sessions (LocalSystem PowerShell processes) persist indefinitely across WebSocket disconnects. No idle timeout or max duration enforced client-side.

---

## 2026-03-30: Per-Customer RSA-PSS Script Signing (Agent Side)

Replaced HMAC-SHA256 script verification with RSA-PSS SHA-256 using per-customer asymmetric keys. Hard cutover — no HMAC fallback.

### What Changed

1. **WiX Installer** — Added `SIGNING_PUBLIC_KEY` Property (server patches PEM into MSI Property table before download, same mechanism as `CUSTOMER_KEY`). New `SigningPublicKeyReg` component writes the PEM to `HKLM\SOFTWARE\CBIT\Agent\SigningPublicKey`.

2. **AgentConfig.cs** — Replaced `script_signing_secret` with `signing_public_key` (string, PEM format with `\n` literals in JSON).

3. **ConfigManager.cs** — Added `GetRegistrySigningPublicKey()` to read PEM from registry on first run and persist to config.json. Removed `UpdateScriptSigningSecret()` and the `scriptSigningSecret` parameter from `UpdateRegistration()`.

4. **ScriptExecutor.cs** — `VerifyScriptSignature` now uses `RSA.Create()` + `ImportFromPem()` + `VerifyData()` with `RSASignaturePadding.Pss` and `HashAlgorithmName.SHA256`. Converts `\n` literals back to real newlines before PEM import. Signature field is base64-encoded (was lowercase hex HMAC). Canonical payload format unchanged: `script_content\n[sorted_variables]\n[sorted_files]`.

5. **Worker.cs** — Removed `ScriptSigningSecret` from registration call and removed `UpdateScriptSigningSecret` call from check-in loop.

6. **CheckInResponse.cs** — Removed `script_signing_secret` field from both `CheckInResponse` and `RegisterResponse`.

### Files Changed
| File | Change |
|------|--------|
| `CbitAgent.Installer/Package.wxs` | Added `SIGNING_PUBLIC_KEY` Property, `SigningPublicKeyReg` ComponentRef |
| `CbitAgent.Installer/Components.wxs` | Added `SigningPublicKeyReg` component (registry write) |
| `Configuration/AgentConfig.cs` | `script_signing_secret` → `signing_public_key` |
| `Configuration/ConfigManager.cs` | Added `GetRegistrySigningPublicKey()`; removed `UpdateScriptSigningSecret()`; simplified `UpdateRegistration()` |
| `Services/ScriptExecutor.cs` | HMAC-SHA256 → RSA-PSS SHA-256 verification |
| `Worker.cs` | Removed HMAC secret wiring |
| `Models/CheckInResponse.cs` | Removed `script_signing_secret` from both response models |

### Server-Side Requirements
- Server must sign scripts with RSA-PSS SHA-256 using per-customer 2048-bit private key
- Signature must be base64-encoded (not hex)
- Server must patch `SIGNING_PUBLIC_KEY` (PEM, newlines as `\n` literals) into MSI Property table alongside `CUSTOMER_KEY`
- Server no longer needs to deliver `script_signing_secret` in registration or check-in responses

---

## 2026-03-23: Security Audit — 7 Hardening Changes

Based on third-party security audit findings. All changes applied in one pass.

### 1. Auto-Update Removed Permanently
- Deleted `AgentUpdater.cs` entirely
- Removed all references from `Worker.cs` (field, constructor, DI, `update_agent` command case, `CheckPendingUpdateResultAsync` call)
- Removed `AgentUpdater` from `Program.cs` DI registration
- Removed `ReportUpdateResultAsync` and `DownloadFileAsync` from `ApiClient.cs` (only used by updater)
- Removed `version`, `download_url`, `file_hash` fields from `AgentCommand` model
- Future updates delivered via PowerShell scripts through Axis
- Also resolves audit findings #2 (version string command injection) and #4 (update hash trust)

### 2. HMAC Signing Extended to Full Script Payload
- `VerifyScriptSignature` now takes the full `PendingScript` object instead of just `script_content`
- Canonical signed payload: `script_content + \n + sorted_variables_json + \n + sorted_files_json`
- Variables: sorted by key as `[["key","value"],...]` JSON array
- Files: sorted by filename as `[["filename","download_url"],...]` JSON array
- Empty variables/files produce `[]` (never omitted)
- **Server must update**: signing algorithm must compute HMAC over the same canonical payload

### 3. Agent Token No Longer Sent to Helper File URLs
- `ScriptExecutor.DownloadFileAsync` no longer attaches `Authorization: Bearer` header
- Domain allowlist enforced: only `axis.gocbit.com` is allowed
- Any external domain URL causes script failure with log entry
- **Server must update**: helper file download endpoint must serve files without auth, or use signed URLs

### 4. Debug Logging of Response Bodies Removed
- `ApiClient.PostWithRetryAsync`: replaced `LogDebug("Response: {Response}", responseJson)` with structured log: status code + byte count only
- Full audit of all `Log*` calls confirmed no tokens, secrets, or credentials are logged at any level

### 5. TLS Certificate Validation Restored (Tray App)
- `TrayApiClient.cs`: removed `ServerCertificateCustomValidationCallback = (_, _, _, _) => true`
- Now uses plain `new HttpClient()` with default OS TLS validation
- Solution-wide grep confirms zero TLS bypasses remain

### 6. Terminal Sessions Use Scoped Session Tokens
- New API endpoint: `POST /api/agent/terminal-session-token` — agent requests a short-lived token before WebSocket connect
- `WebSocketTerminalClient` sends `session_token` (not `agent_token`) in the WebSocket auth message
- Falls back to `agent_token` if server doesn't support session tokens yet (graceful migration)
- **Server must implement**: `POST /api/agent/terminal-session-token` — accept agent bearer token, return `{ session_token: "..." }` with 2-hour expiry. WebSocket handler must accept `session_token` in auth message.

### 7. MSI Build Integrity — Hash Verification
- New `build-manifest.json`: stores SHA256 hashes of published binaries (checked into source control)
- New `build-and-verify.ps1`: publishes binaries, verifies hashes against manifest, builds MSI
- Run `.\build-and-verify.ps1 -UpdateManifest` after intentional code changes to update hashes
- MSI build fails with clear error if binary hashes don't match manifest

### Files Changed
| File | Change |
|------|--------|
| `Services/AgentUpdater.cs` | **Deleted** |
| `Worker.cs` | Removed AgentUpdater field, constructor param, DI, update_agent case, pending update check |
| `Program.cs` | Removed `AddSingleton<AgentUpdater>()` |
| `Services/ApiClient.cs` | Removed `ReportUpdateResultAsync`, `DownloadFileAsync`; replaced response body debug log with structured log; added `RequestTerminalSessionTokenAsync` |
| `Models/CheckInResponse.cs` | Removed `Version`, `DownloadUrl`, `FileHash` from `AgentCommand` |
| `Services/ScriptExecutor.cs` | Extended `VerifyScriptSignature` to cover full payload; removed auth header from helper downloads; added domain allowlist |
| `Services/WebSocketTerminalClient.cs` | Requests terminal session token; sends `session_token` instead of `agent_token` in WS auth |
| `CbitAgent.Tray/TrayApiClient.cs` | Removed TLS bypass |
| `build-manifest.json` | **New** — binary hash manifest |
| `build-and-verify.ps1` | **New** — build + hash verification script |

### Server-Side Changes Required (for CC Linux)
1. **HMAC signing**: Update script signing to compute HMAC over canonical payload: `script_content\n[sorted_variables]\n[sorted_files]`
2. **Helper file downloads**: Serve helper files without requiring auth header (use signed URLs or unauthenticated download endpoint)
3. **Terminal session tokens**: Implement `POST /api/agent/terminal-session-token` — returns `{ session_token: "short-lived-jwt" }`. WebSocket handler must accept `session_token` in auth message alongside existing `agent_token` during migration.

---

## 2026-03-29: MSI Add/Remove Programs Icon

Added Axis favicon.ico as the ARP (Add/Remove Programs) icon for the MSI installer.

**Changes:**
- `CbitAgent.Installer/Package.wxs` — Added `<Icon Id="AxisIcon.ico" SourceFile="favicon.ico" />`, `ARPPRODUCTICON`, and `ARPHELPLINK` properties
- `CbitAgent.Installer/favicon.ico` — Icon file (4,286 bytes) placed by hand before build

CBIT RMM Agent now shows the Axis icon in Windows Settings → Apps and Control Panel → Programs and Features, with a help link to https://axis.gocbit.com.

---

## 2026-03-29: ScreenConnect Session GUID Collection

**Added** — Agent now reads the ScreenConnect **session GUID** (the `s=` parameter) from the service's registry `ImagePath`, instead of parsing the instance ID from the service name.

**How it works:**
1. Config field `screenconnect_instance_id` specifies which ScreenConnect instance to look up (default: `8646a2c674847db0`)
2. Reads registry: `HKLM\SYSTEM\CurrentControlSet\Services\ScreenConnect Client ({instanceId})\ImagePath`
3. Extracts the `s=` parameter (UUID format) from the ImagePath command-line arguments
4. Sends as `screenconnect_guid` in check-in payload (string UUID or null)
5. Server can deliver `screenconnect_instance_id` in check-in response to override the default

**Files changed:**
- `Configuration/AgentConfig.cs` — Added `screenconnect_instance_id` field
- `Configuration/ConfigManager.cs` — Added `UpdateScreenConnectInstanceId()` method
- `Models/CheckInResponse.cs` — Added `screenconnect_instance_id` field for server delivery
- `Services/ScreenConnectDetector.cs` — Rewritten: reads registry ImagePath, extracts `s=` session GUID via regex
- `Worker.cs` — Passes `config.ScreenConnectInstanceId` to detector; persists server-delivered instance ID

**Check-in payload field:** `screenconnect_guid` (unchanged field name, now contains the session GUID instead of instance ID)

---

## 2026-03-23: TLS Bypass Removed from Tray App

_(Superseded by Security Audit item #5 above — kept for historical reference)_

---

## 2026-03-22: Network Adapter Collection Rewrite

**Rewritten** — network adapter collection now reports ALL adapters (physical, virtual, VPN, Hyper-V, tunnel, loopback) with all bound IP addresses per adapter.

**Changes:**
- `Models/NetworkAdapter.cs` — Replaced single `ip_address`/`subnet_mask` with `addresses` array of `{ip, subnet}` objects. Renamed `mac_address` → `mac`, `default_gateway` → `gateway`, `dhcp_enabled` → `dhcp` to match requested JSON structure. Added `AdapterAddress` model.
- `Services/NetworkInfoCollector.cs` — Removed filter that excluded Loopback/Tunnel adapters. Removed "skip if no IPv4" check. Now iterates all `UnicastAddresses` per adapter (IPv4 and IPv6). Type mapping expanded: Loopback, Tunnel, VPN (PPP), plus `type.ToString()` fallback.

**JSON structure:**
```json
{
  "name": "Intel(R) Ethernet Connection",
  "type": "Ethernet",
  "mac": "C8:F7:50:FB:9D:46",
  "dhcp": true,
  "gateway": "192.168.1.1",
  "addresses": [
    { "ip": "192.168.1.57", "subnet": "255.255.255.0" },
    { "ip": "fe80::1234:5678:abcd:ef01", "subnet": null }
  ],
  "dns_servers": ["8.8.8.8"],
  "is_primary": true,
  "wifi_ssid": null,
  "wifi_signal_strength": null,
  "wifi_link_speed": null,
  "wifi_frequency_band": null
}
```

**Breaking change:** Server must expect `mac`/`dhcp`/`gateway`/`addresses` instead of old `mac_address`/`dhcp_enabled`/`default_gateway`/`ip_address`/`subnet_mask`.

## 2026-03-22: New Check-in Telemetry (7 Data Points)

Added 6 new fields to check-in payload (`uptime_seconds` already existed in `system_info`):

1. **cpu_usage** (float, 0-100) — `PerformanceCounter("Processor", "% Processor Time", "_Total")` with 500ms delay (first read is always 0)
2. **ram_usage** (float, 0-100) — WMI `Win32_OperatingSystem` TotalVisibleMemorySize vs FreePhysicalMemory
3. **uptime_seconds** (long) — Already existed in `system_info` from `SystemInfoCollector`
4. **pending_reboot** (boolean) — Checks 4 registry locations: CBS\RebootPending, WU\RebootRequired, Session Manager\PendingFileRenameOperations, Updates\UpdateExeVolatile
5. **defender_enabled** (bool|null), **defender_definitions_date** (ISO date|null), **defender_last_scan_days** (int|null) — WMI `MSFT_MpComputerStatus` from `root\Microsoft\Windows\Defender`. Nulls if Defender not installed.
6. **bitlocker_status** (array) — WMI `Win32_EncryptableVolume` from `root\cimv2\Security\MicrosoftVolumeEncryption`. Empty array if namespace missing (Home editions).
7. **local_admins** (string array) — WinNT ADSI provider, `DOMAIN\username` format, excludes built-in Administrator account.

**Files changed:**
- `Models/CheckInPayload.cs` — 8 new fields + `BitLockerDrive` model
- `Services/SystemInfoCollector.cs` — 6 new collection methods, each in try/catch
- `Worker.cs` — Wire new collectors into `PerformCheckInAsync`
- `CbitAgent.csproj` — Added `System.DirectoryServices` and `System.Diagnostics.PerformanceCounter` packages

Every collection method has its own try/catch — if any single item fails, it sends null and the rest of the check-in proceeds normally.

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
