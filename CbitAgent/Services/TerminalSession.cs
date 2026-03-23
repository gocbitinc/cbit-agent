using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CbitAgent.Services;

/// <summary>
/// Manages a single PowerShell process for one terminal session.
/// Reads stdout/stderr as raw bytes (not line-buffered) so prompts and partial
/// output arrive immediately. Input echo is handled by the caller.
/// </summary>
public class TerminalSession : IDisposable
{
    private readonly string _sessionId;
    private readonly ILogger _logger;
    private readonly Func<string, string, Task> _onOutput; // (sessionId, data) → send to server
    private readonly Func<string, string, Task> _onError;  // (sessionId, error) → send error to server
    private readonly CancellationTokenSource _cts = new();
    private Process? _process;
    private bool _disposed;

    public string SessionId => _sessionId;

    public TerminalSession(
        string sessionId,
        ILogger logger,
        Func<string, string, Task> onOutput,
        Func<string, string, Task> onError)
    {
        _sessionId = sessionId;
        _logger = logger;
        _onOutput = onOutput;
        _onError = onError;

        try
        {
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoLogo -NoProfile -NonInteractive -Command -",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = @"C:\"
                },
                EnableRaisingEvents = true
            };

            _process.Exited += OnProcessExited;

            _process.Start();

            // Raw byte-level reading — no line buffering, so prompts show immediately
            _ = ReadStreamAsync(_process.StandardOutput.BaseStream);
            _ = ReadStreamAsync(_process.StandardError.BaseStream);

            _logger.LogInformation("Terminal session {SessionId}: started powershell.exe (PID {Pid})",
                _sessionId, _process.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Terminal session {SessionId}: failed to start PowerShell", _sessionId);
            _ = _onError(_sessionId, $"Failed to start PowerShell: {ex.Message}");
            _process = null;
        }
    }

    public bool IsRunning => _process != null && !_process.HasExited;

    public void WriteInput(string data)
    {
        if (_process == null || _process.HasExited) return;

        try
        {
            _process.StandardInput.Write(data);
            _process.StandardInput.Flush();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Terminal session {SessionId}: failed to write input", _sessionId);
        }
    }

    public void Kill()
    {
        if (_process == null) return;

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _logger.LogInformation("Terminal session {SessionId}: process killed", _sessionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Terminal session {SessionId}: error killing process", _sessionId);
        }
    }

    /// <summary>
    /// Reads raw bytes from a stream (stdout or stderr) and forwards them
    /// immediately. Unlike BeginOutputReadLine, this doesn't wait for newlines.
    /// </summary>
    private async Task ReadStreamAsync(Stream stream)
    {
        var buffer = new byte[4096];
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, _cts.Token);
                if (bytesRead == 0) break; // stream closed
                var text = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                await _onOutput(_sessionId, text);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Terminal session {SessionId}: stream read error", _sessionId);
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        _logger.LogInformation("Terminal session {SessionId}: process exited with code {Code}",
            _sessionId, _process?.ExitCode);
        _ = _onOutput(_sessionId, $"\r\n[Process exited with code {_process?.ExitCode}]\r\n");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        Kill();

        if (_process != null)
        {
            _process.Exited -= OnProcessExited;
            _process.Dispose();
            _process = null;
        }

        _cts.Dispose();
    }
}
