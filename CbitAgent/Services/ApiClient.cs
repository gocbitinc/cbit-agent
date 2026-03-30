using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CbitAgent.Configuration;
using CbitAgent.Models;
using Microsoft.Extensions.Logging;

namespace CbitAgent.Services;

public class ApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ConfigManager _configManager;
    private readonly ILogger<ApiClient> _logger;
    private const int MaxRetries = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ApiClient(ConfigManager configManager, ILogger<ApiClient> logger)
    {
        _configManager = configManager;
        _logger = logger;

        _httpClient = new HttpClient()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    private string BaseUrl => _configManager.Config.ServerUrl.TrimEnd('/') + "/api/agent";

    private void SetAuthHeaders()
    {
        var config = _configManager.Config;
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", config.AgentToken);

        if (_httpClient.DefaultRequestHeaders.Contains("X-Agent-ID"))
            _httpClient.DefaultRequestHeaders.Remove("X-Agent-ID");
        _httpClient.DefaultRequestHeaders.Add("X-Agent-ID", config.AgentId);
    }

    public async Task<RegisterResponse?> RegisterAsync(string customerKey, string hostname,
        string osType, string osVersion, string agentVersion, CancellationToken ct = default)
    {
        var payload = new
        {
            customer_key = customerKey,
            hostname,
            os_type = osType,
            os_version = osVersion,
            agent_version = agentVersion
        };

        return await PostWithRetryAsync<RegisterResponse>($"{BaseUrl}/register", payload, useAuth: false, ct);
    }

    public async Task<CheckInResponse?> CheckInAsync(CheckInPayload payload, CancellationToken ct = default)
    {
        SetAuthHeaders();
        return await PostWithRetryAsync<CheckInResponse>($"{BaseUrl}/checkin", payload, useAuth: true, ct);
    }

    public async Task<bool> ReportAppsAsync(string agentId, List<InstalledApp> apps, CancellationToken ct = default)
    {
        SetAuthHeaders();
        var payload = new { agent_id = agentId, apps };
        var result = await PostWithRetryAsync<JsonElement>($"{BaseUrl}/apps", payload, useAuth: true, ct);
        return result.ValueKind != JsonValueKind.Undefined;
    }

    public async Task<bool> ReportPatchesAsync(string agentId, List<InstalledPatch> installed,
        List<PendingPatch> pending, CancellationToken ct = default)
    {
        SetAuthHeaders();
        var payload = new
        {
            agent_id = agentId,
            installed_patches = installed,
            pending_patches = pending
        };
        var result = await PostWithRetryAsync<JsonElement>($"{BaseUrl}/patches", payload, useAuth: true, ct);
        return result.ValueKind != JsonValueKind.Undefined;
    }

    public async Task<bool> ReportUpdateJobResultAsync(
        string agentId, string jobType, string? policyId, string? adhocJobId,
        DateTime startedAt, DateTime completedAt, string status,
        List<string> kbsInstalled, List<string> kbsFailed,
        string? errorMessage, bool rebootRequired, CancellationToken ct = default)
    {
        SetAuthHeaders();
        var payload = new
        {
            agent_id = agentId,
            job_type = jobType,
            policy_id = policyId,
            adhoc_job_id = adhocJobId,
            started_at = startedAt,
            completed_at = completedAt,
            status,
            kbs_installed = kbsInstalled,
            kbs_failed = kbsFailed,
            error_message = errorMessage,
            reboot_required = rebootRequired
        };
        var result = await PostWithRetryAsync<JsonElement>($"{BaseUrl}/update-result", payload, useAuth: true, ct);
        return result.ValueKind != JsonValueKind.Undefined;
    }

    public async Task<bool> ReportScriptResultAsync(string executionId, ScriptResult result, CancellationToken ct = default)
    {
        SetAuthHeaders();
        var payload = new
        {
            status = result.Status,
            exit_code = result.ExitCode,
            stdout = result.Stdout,
            stderr = result.Stderr,
            started_at = result.StartedAt.ToString("o"),
            completed_at = result.CompletedAt.ToString("o")
        };
        var response = await PostWithRetryAsync<System.Text.Json.JsonElement>(
            $"{BaseUrl}/scripts/{executionId}/result", payload, useAuth: true, ct);
        return response.ValueKind != System.Text.Json.JsonValueKind.Undefined;
    }

    /// <summary>
    /// Requests a short-lived terminal session token from the server.
    /// The token is scoped to WebSocket terminal sessions only.
    /// Returns the session token string, or null on failure.
    /// </summary>
    public async Task<string?> RequestTerminalSessionTokenAsync(CancellationToken ct = default)
    {
        SetAuthHeaders();
        var payload = new { agent_id = _configManager.Config.AgentId };
        var result = await PostWithRetryAsync<JsonElement>($"{BaseUrl}/terminal-session-token", payload, useAuth: true, ct);
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("session_token", out var tokenProp))
        {
            return tokenProp.GetString();
        }
        return null;
    }

    public async Task PostAlertsAsync(List<object> alerts, CancellationToken ct = default)
    {
        try
        {
            SetAuthHeaders();
            var json = JsonSerializer.Serialize(alerts, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            _logger.LogDebug("POST {Url}/alerts ({Count} alerts)", BaseUrl, alerts.Count);
            using var response = await _httpClient.PostAsync($"{BaseUrl}/alerts", content, ct);
            response.EnsureSuccessStatusCode();
            _logger.LogInformation("Posted {Count} alerts successfully", alerts.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to post {Count} alerts", alerts.Count);
        }
    }

    private async Task<T> PostWithRetryAsync<T>(string url, object payload, bool useAuth, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                var json = JsonSerializer.Serialize(payload, JsonOptions);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                _logger.LogDebug("POST {Url} (attempt {Attempt})", url, attempt);

                using var response = await _httpClient.PostAsync(url, content, ct);

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    _logger.LogError("Server returned 401 Unauthorized. Agent token may be invalid.");
                    return default!;
                }

                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync(ct);
                _logger.LogDebug("POST {Url} returned {StatusCode} ({Length} bytes)",
                    url, (int)response.StatusCode, responseJson.Length);

                var result = JsonSerializer.Deserialize<T>(responseJson, JsonOptions);
                return result ?? default!;
            }
            catch (HttpRequestException ex) when (attempt < MaxRetries && !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Request to {Url} failed (attempt {Attempt}), retrying...", url, attempt);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogInformation("Request cancelled");
                return default!;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Request to {Url} failed after {Attempts} attempts", url, attempt);
                return default!;
            }
        }

        return default!;
    }
}
