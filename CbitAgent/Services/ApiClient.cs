using System.Net.Http.Headers;
using System.Net.Http.Json;
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

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true
        };

        _httpClient = new HttpClient(handler)
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

    public async Task<bool> ReportUpdateResultAsync(string agentId, string fromVersion,
        string toVersion, string status, string? errorMessage, CancellationToken ct = default)
    {
        SetAuthHeaders();
        var payload = new
        {
            agent_id = agentId,
            from_version = fromVersion,
            to_version = toVersion,
            status,
            error_message = errorMessage
        };
        var result = await PostWithRetryAsync<JsonElement>($"{BaseUrl}/update-agent-result", payload, useAuth: true, ct);
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

    public async Task<bool> ReportScriptResultAsync(int executionId, ScriptResult result, CancellationToken ct = default)
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

    public async Task<bool> DownloadFileAsync(string relativeUrl, string destinationPath, CancellationToken ct = default)
    {
        var url = _configManager.Config.ServerUrl.TrimEnd('/') + relativeUrl;
        SetAuthHeaders();

        for (int attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                _logger.LogInformation("Downloading file from {Url} (attempt {Attempt})", url, attempt);

                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var dir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write);
                await stream.CopyToAsync(fileStream, ct);

                _logger.LogInformation("Downloaded file to {Path}", destinationPath);
                return true;
            }
            catch (Exception ex) when (attempt < MaxRetries && !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Download attempt {Attempt} failed, retrying...", attempt);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Download failed after {MaxRetries} attempts", MaxRetries);
                return false;
            }
        }

        return false;
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
                _logger.LogDebug("Response: {Response}", responseJson);

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
