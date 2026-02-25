using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CbitAgent.Configuration;
using CbitAgent.Models;
using Microsoft.Extensions.Logging;

namespace CbitAgent.Services;

/// <summary>
/// Maintains a persistent WebSocket connection to the server for real-time
/// terminal sessions. Reconnects with exponential backoff on failure.
/// Manages multiple concurrent sessions via ConcurrentDictionary.
/// </summary>
public class WebSocketTerminalClient : IDisposable
{
    private readonly ConfigManager _configManager;
    private readonly ILogger<WebSocketTerminalClient> _logger;
    private readonly WindowsUpdateExecutor _updateExecutor;
    private readonly PatchInfoCollector _patchCollector;
    private readonly ApiClient _apiClient;
    private readonly ConcurrentDictionary<string, TerminalSession> _sessions = new();

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _receiveCts;
    private bool _disposed;

    private const int MaxBackoffSeconds = 60;
    private const int ReceiveBufferSize = 8192;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public WebSocketTerminalClient(
        ConfigManager configManager,
        ILogger<WebSocketTerminalClient> logger,
        WindowsUpdateExecutor updateExecutor,
        PatchInfoCollector patchCollector,
        ApiClient apiClient)
    {
        _configManager = configManager;
        _logger = logger;
        _updateExecutor = updateExecutor;
        _patchCollector = patchCollector;
        _apiClient = apiClient;
    }

    /// <summary>
    /// Runs the WebSocket connection loop. Connects, receives messages, and
    /// reconnects with exponential backoff on disconnection. Call this as a
    /// long-running Task alongside the HTTP check-in loop.
    /// </summary>
    public async Task RunAsync(CancellationToken stoppingToken)
    {
        int attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAndRunAsync(stoppingToken);
                // Clean disconnect — reset backoff
                attempt = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                attempt++;
                var delaySec = Math.Min((int)Math.Pow(2, attempt), MaxBackoffSeconds);
                _logger.LogWarning(ex, "WebSocket disconnected (attempt {Attempt}), reconnecting in {Delay}s",
                    attempt, delaySec);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySec), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                CleanupConnection();
            }
        }

        // Dispose all sessions on shutdown
        foreach (var kvp in _sessions)
        {
            kvp.Value.Dispose();
        }
        _sessions.Clear();

        _logger.LogInformation("WebSocket terminal client stopped");
    }

    private async Task ConnectAndRunAsync(CancellationToken stoppingToken)
    {
        var config = _configManager.Config;
        if (!config.IsRegistered)
        {
            _logger.LogDebug("Agent not registered, skipping WebSocket connection");
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            return;
        }

        // Build WSS URL: https://... → wss://...
        var baseUrl = config.ServerUrl.TrimEnd('/');
        var wsUrl = baseUrl
            .Replace("https://", "wss://")
            .Replace("http://", "ws://");
        var uri = new Uri($"{wsUrl}/api/agent/ws?token={config.AgentToken}");

        _ws = new ClientWebSocket();
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        _logger.LogInformation("WebSocket connecting to {Url}...", $"{wsUrl}/api/agent/ws");

        await _ws.ConnectAsync(uri, stoppingToken);

        _logger.LogInformation("WebSocket connected");

        // Send auth message
        await SendMessageAsync(new WsOutMessage
        {
            Type = "auth",
            Data = config.AgentId
        }, stoppingToken);

        // Receive loop
        _receiveCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        await ReceiveLoopAsync(_receiveCts.Token);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[ReceiveBufferSize];
        var messageBuffer = new List<byte>();

        while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            }
            catch (WebSocketException ex)
            {
                _logger.LogWarning(ex, "WebSocket receive error");
                break;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                _logger.LogInformation("WebSocket server sent close frame");
                try
                {
                    await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                }
                catch { }
                break;
            }

            messageBuffer.AddRange(new ArraySegment<byte>(buffer, 0, result.Count));

            if (result.EndOfMessage)
            {
                var json = Encoding.UTF8.GetString(messageBuffer.ToArray());
                messageBuffer.Clear();

                try
                {
                    await HandleMessageAsync(json, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling WebSocket message");
                }
            }
        }
    }

    private async Task HandleMessageAsync(string json, CancellationToken ct)
    {
        // Log every incoming message at Info level for debugging
        _logger.LogInformation("Received WebSocket message: {Json}", json);

        var msg = JsonSerializer.Deserialize<WsMessage>(json, JsonOpts);
        if (msg == null)
        {
            _logger.LogWarning("Failed to deserialize WebSocket message");
            return;
        }

        _logger.LogInformation("Parsed WebSocket message type: {Type}", msg.Type);

        switch (msg.Type)
        {
            case "terminal_start":
                await HandleTerminalStart(msg, ct);
                break;

            case "terminal_input":
                await HandleTerminalInput(msg, ct);
                break;

            case "terminal_stop":
                HandleTerminalStop(msg);
                break;

            case "terminal_resize":
                // Resize is informational for PTY-based terminals.
                // cmd/powershell via stdin/stdout don't support resize natively,
                // but we log it for future ConPTY support.
                _logger.LogDebug("Terminal resize for {SessionId}: {Cols}x{Rows}",
                    msg.SessionId, msg.Cols, msg.Rows);
                break;

            case "scan_updates":
                _ = HandleScanUpdates(msg, ct);
                break;

            case "install_kb":
                _ = HandleInstallKb(msg, ct);
                break;

            case "install_updates":
                _ = HandleInstallUpdates(msg, ct);
                break;

            case "reboot":
                await HandleReboot(ct);
                break;

            case "ping":
                await SendMessageAsync(new WsOutMessage { Type = "pong" }, ct);
                break;

            default:
                _logger.LogDebug("Unknown WebSocket message type: {Type}", msg.Type);
                break;
        }
    }

    private async Task HandleTerminalStart(WsMessage msg, CancellationToken ct)
    {
        var sessionId = msg.SessionId;
        if (string.IsNullOrEmpty(sessionId)) return;

        var shellType = msg.ShellType ?? "powershell";

        _logger.LogInformation("Starting terminal session {SessionId} ({Shell})", sessionId, shellType);

        // Kill existing session with same ID if any
        if (_sessions.TryRemove(sessionId, out var existing))
        {
            existing.Dispose();
        }

        var session = new TerminalSession(
            sessionId,
            shellType,
            _logger,
            onOutput: async (sid, data) => await SendMessageAsync(
                new WsOutMessage { Type = "terminal_output", SessionId = sid, Data = data }, ct),
            onError: async (sid, error) => await SendMessageAsync(
                new WsOutMessage { Type = "terminal_error", SessionId = sid, Error = error }, ct)
        );

        if (session.IsRunning)
        {
            _sessions[sessionId] = session;
            await SendMessageAsync(new WsOutMessage
            {
                Type = "terminal_started",
                SessionId = sessionId
            }, ct);
        }
        else
        {
            session.Dispose();
            await SendMessageAsync(new WsOutMessage
            {
                Type = "terminal_error",
                SessionId = sessionId,
                Error = "Failed to start shell process"
            }, ct);
        }
    }

    private async Task HandleTerminalInput(WsMessage msg, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(msg.SessionId) || msg.Data == null) return;

        if (_sessions.TryGetValue(msg.SessionId, out var session))
        {
            // Echo input back so the user sees what they type.
            // Redirected-stdin processes don't echo natively.
            var echo = BuildEcho(msg.Data);
            if (echo.Length > 0)
            {
                await SendMessageAsync(new WsOutMessage
                {
                    Type = "terminal_output",
                    SessionId = msg.SessionId,
                    Data = echo
                }, ct);
            }

            session.WriteInput(msg.Data);
        }
        else
        {
            _logger.LogWarning("terminal_input for unknown session {SessionId}", msg.SessionId);
        }
    }

    /// <summary>
    /// Builds the echo string for terminal input. Handles Enter, Backspace,
    /// and printable characters. Suppresses echo for escape sequences (arrow keys, etc.).
    /// </summary>
    private static string BuildEcho(string input)
    {
        // Don't echo escape sequences (arrow keys, function keys, etc.)
        if (input.Length > 0 && input[0] == '\x1b')
            return string.Empty;

        var sb = new StringBuilder(input.Length * 2);
        foreach (var ch in input)
        {
            switch (ch)
            {
                case '\r':
                    sb.Append("\r\n");
                    break;
                case '\n':
                    // Ignore \n if it follows \r (already handled above)
                    break;
                case '\x7f': // DEL — xterm.js sends this for Backspace
                    sb.Append("\b \b");
                    break;
                case '\b':   // BS
                    sb.Append("\b \b");
                    break;
                default:
                    if (ch >= ' ') // printable characters only
                        sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    // ─── Windows Update handlers ─────────────────────────────────────

    private async Task HandleScanUpdates(WsMessage msg, CancellationToken ct)
    {
        var jobId = msg.JobId;
        _logger.LogInformation("Received scan_updates command (job={JobId})", jobId);

        try
        {
            var patches = await _updateExecutor.ScanAsync(ct);

            await SendMessageAsync(new WsOutMessage
            {
                Type = "scan_result",
                JobId = jobId,
                PendingPatches = patches,
                RebootRequired = _updateExecutor.CheckRebootRequired()
            }, ct);

            _logger.LogInformation("Scan complete: {Count} pending updates", patches.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "scan_updates failed");
            await SendMessageAsync(new WsOutMessage
            {
                Type = "scan_result",
                JobId = jobId,
                Error = ex.Message
            }, ct);
        }
    }

    private async Task HandleInstallKb(WsMessage msg, CancellationToken ct)
    {
        var jobId = msg.JobId;
        var jobAssetId = msg.JobAssetId;
        var kbNumber = msg.KbNumber;

        _logger.LogInformation("install_kb handler entered. JobId={JobId}, JobAssetId={JobAssetId}, KbNumber={Kb}",
            jobId ?? "(null)", jobAssetId ?? "(null)", kbNumber ?? "(null)");

        if (string.IsNullOrEmpty(kbNumber))
        {
            _logger.LogWarning("install_kb: No KB number provided");
            await SendMessageAsync(new WsOutMessage
            {
                Type = "patch_status",
                JobId = jobId,
                JobAssetId = jobAssetId,
                KbNumber = kbNumber,
                Status = "failed",
                Error = "No KB number provided"
            }, ct);
            return;
        }

        _logger.LogInformation("Starting install for {Kb} (job={JobId}, jobAsset={JobAssetId})",
            kbNumber, jobId, jobAssetId);

        try
        {
            // Step 1: Send "downloading" status
            _logger.LogInformation("install_kb: Sending downloading status for {Kb}", kbNumber);
            await SendMessageAsync(new WsOutMessage
            {
                Type = "patch_status",
                JobId = jobId,
                JobAssetId = jobAssetId,
                KbNumber = kbNumber,
                Status = "downloading"
            }, ct);

            // Step 2: Install the KB (executor handles scan, download, and install)
            Func<UpdateProgress, Task> onProgress = async progress =>
            {
                _logger.LogInformation("install_kb progress for {Kb}: {Status} {Percent}%",
                    kbNumber, progress.Status, progress.ProgressPercent);

                // When download is done and install begins, send "installing"
                if (progress.Status == "installing")
                {
                    await SendMessageAsync(new WsOutMessage
                    {
                        Type = "patch_status",
                        JobId = jobId,
                        JobAssetId = jobAssetId,
                        KbNumber = kbNumber,
                        Status = "installing",
                        ProgressPercent = progress.ProgressPercent
                    }, ct);
                }
            };

            _logger.LogInformation("install_kb: Calling WindowsUpdateExecutor for {Kb}...", kbNumber);
            var result = await _updateExecutor.InstallUpdatesAsync(
                new List<string> { kbNumber }, msg.RebootBehavior, onProgress, ct);

            // Step 3: Send final status
            var finalStatus = result.KbsInstalled.Contains(kbNumber) ? "completed" : "failed";
            var errorMsg = result.KbsFailed.Count > 0 ? result.ErrorMessage : null;

            _logger.LogInformation("install_kb finished for {Kb}: Status={Status}, Installed=[{Installed}], Failed=[{Failed}], RebootRequired={Reboot}",
                kbNumber, finalStatus,
                string.Join(", ", result.KbsInstalled),
                string.Join(", ", result.KbsFailed),
                result.RebootRequired);

            await SendMessageAsync(new WsOutMessage
            {
                Type = "patch_status",
                JobId = jobId,
                JobAssetId = jobAssetId,
                KbNumber = kbNumber,
                Status = finalStatus,
                RebootRequired = result.RebootRequired,
                Error = errorMsg
            }, ct);

            // Step 4: Trigger fresh patch scan and report
            _logger.LogInformation("install_kb: Triggering fresh patch scan and report...");
            try
            {
                var agentId = _configManager.Config.AgentId;
                if (!string.IsNullOrEmpty(agentId))
                {
                    var installed = _patchCollector.CollectInstalledPatches();
                    var pending = _patchCollector.CollectPendingPatches();
                    var reported = await _apiClient.ReportPatchesAsync(agentId, installed, pending, ct);
                    _logger.LogInformation("install_kb: Patch report sent ({Installed} installed, {Pending} pending, success={Success})",
                        installed.Count, pending.Count, reported);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "install_kb: Failed to send post-install patch report");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "install_kb failed for {Kb} (job={JobId})", kbNumber, jobId);
            await SendMessageAsync(new WsOutMessage
            {
                Type = "patch_status",
                JobId = jobId,
                JobAssetId = jobAssetId,
                KbNumber = kbNumber,
                Status = "failed",
                Error = ex.Message,
                RebootRequired = _updateExecutor.CheckRebootRequired()
            }, ct);
        }
    }

    private async Task HandleInstallUpdates(WsMessage msg, CancellationToken ct)
    {
        var jobId = msg.JobId;
        var kbNumbers = msg.KbNumbers;

        _logger.LogInformation("install_updates handler entered. JobId={JobId}, KbNumbers={Kbs}, RebootBehavior={Reboot}",
            jobId ?? "(null)", kbNumbers != null ? string.Join(", ", kbNumbers) : "(null)", msg.RebootBehavior ?? "(null)");

        if (kbNumbers == null || kbNumbers.Count == 0)
        {
            _logger.LogWarning("install_updates: No KB numbers provided, sending failed result");
            await SendMessageAsync(new WsOutMessage
            {
                Type = "update_result",
                JobId = jobId,
                Status = "failed",
                Error = "No KB numbers provided"
            }, ct);
            return;
        }

        _logger.LogInformation("install_updates: Starting installation of {Count} KBs (job={JobId}): {Kbs}",
            kbNumbers.Count, jobId, string.Join(", ", kbNumbers));

        // Send progress updates over WebSocket
        Func<UpdateProgress, Task> onProgress = async progress =>
        {
            _logger.LogInformation("install_updates progress: {Status} {Kb} {Percent}% (job={JobId})",
                progress.Status, progress.CurrentKb, progress.ProgressPercent, jobId);
            await SendMessageAsync(new WsOutMessage
            {
                Type = "update_progress",
                JobId = jobId,
                Status = progress.Status,
                CurrentKb = progress.CurrentKb,
                ProgressPercent = progress.ProgressPercent
            }, ct);
        };

        try
        {
            _logger.LogInformation("install_updates: Calling WindowsUpdateExecutor.InstallUpdatesAsync...");
            var result = await _updateExecutor.InstallUpdatesAsync(
                kbNumbers, msg.RebootBehavior, onProgress, ct);

            _logger.LogInformation("install_updates complete (job={JobId}): Status={Status}, Installed=[{Installed}], Failed=[{Failed}], RebootRequired={Reboot}",
                jobId, result.Status,
                string.Join(", ", result.KbsInstalled),
                string.Join(", ", result.KbsFailed),
                result.RebootRequired);

            await SendMessageAsync(new WsOutMessage
            {
                Type = "update_result",
                JobId = jobId,
                Status = result.Status,
                KbsInstalled = result.KbsInstalled,
                KbsFailed = result.KbsFailed,
                RebootRequired = result.RebootRequired,
                Error = result.ErrorMessage
            }, ct);

            _logger.LogInformation("install_updates: Sent update_result to server (job={JobId})", jobId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "install_updates failed (job={JobId})", jobId);
            await SendMessageAsync(new WsOutMessage
            {
                Type = "update_result",
                JobId = jobId,
                Status = "failed",
                Error = ex.Message,
                RebootRequired = _updateExecutor.CheckRebootRequired()
            }, ct);
        }
    }

    // ─── Reboot handler ─────────────────────────────────────────────

    private async Task HandleReboot(CancellationToken ct)
    {
        _logger.LogInformation("Reboot command received, rebooting in 10 seconds");

        await SendMessageAsync(new WsOutMessage
        {
            Type = "reboot_status",
            Status = "rebooting"
        }, ct);

        System.Diagnostics.Process.Start("shutdown", "/r /t 10 /f");
    }

    // ─── Terminal handlers ────────────────────────────────────────────

    private void HandleTerminalStop(WsMessage msg)
    {
        if (string.IsNullOrEmpty(msg.SessionId)) return;

        _logger.LogInformation("Stopping terminal session {SessionId}", msg.SessionId);

        if (_sessions.TryRemove(msg.SessionId, out var session))
        {
            session.Dispose();
        }
    }

    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private async Task SendMessageAsync(WsOutMessage message, CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open) return;

        var json = JsonSerializer.Serialize(message, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(ct);
        try
        {
            await _ws.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send WebSocket message");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void CleanupConnection()
    {
        // Cancel the receive loop but don't dispose sessions —
        // they persist across reconnections (running processes stay alive)
        _receiveCts?.Cancel();
        _receiveCts?.Dispose();
        _receiveCts = null;

        if (_ws != null)
        {
            try { _ws.Dispose(); } catch { }
            _ws = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _receiveCts?.Cancel();
        CleanupConnection();

        foreach (var kvp in _sessions)
        {
            kvp.Value.Dispose();
        }
        _sessions.Clear();

        _sendLock.Dispose();
    }
}
