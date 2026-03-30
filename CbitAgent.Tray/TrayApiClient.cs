using System.Net.Http.Headers;
using System.Text.Json;

namespace CbitAgent.Tray;

/// <summary>
/// Reads agent config and submits support requests as multipart/form-data.
/// </summary>
public class TrayApiClient
{
    private const string ConfigPath = @"C:\Program Files\CBIT\Agent\config.json";

    private readonly string? _serverUrl;
    private readonly string? _agentId;
    private readonly string? _agentToken;

    public bool IsConfigured => !string.IsNullOrEmpty(_serverUrl)
                                && !string.IsNullOrEmpty(_agentId)
                                && !string.IsNullOrEmpty(_agentToken);

    public TrayApiClient()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;

            var json = File.ReadAllText(ConfigPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("server_url", out var urlProp))
                _serverUrl = urlProp.GetString();
            if (root.TryGetProperty("agent_id", out var idProp))
                _agentId = idProp.GetString();
            if (root.TryGetProperty("agent_token", out var tokenProp))
                _agentToken = tokenProp.GetString();
        }
        catch
        {
            // Config not available — IsConfigured will be false
        }
    }

    /// <summary>
    /// Submits a support request with optional screenshot.
    /// Returns (success, ticketNumber, errorMessage).
    /// </summary>
    public async Task<(bool Success, string? TicketNumber, string? ErrorMessage)> SubmitSupportRequestAsync(
        string description, string windowsUsername, string? email, byte[]? screenshotPng)
    {
        if (!IsConfigured)
            return (false, null, "Agent is not configured or registered");

        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _agentToken);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(_agentId!), "agent_id");
        form.Add(new StringContent(description), "description");
        form.Add(new StringContent(windowsUsername), "windows_username");

        if (!string.IsNullOrEmpty(email))
            form.Add(new StringContent(email), "email");

        if (screenshotPng != null)
        {
            var fileContent = new ByteArrayContent(screenshotPng);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(fileContent, "screenshot", "screenshot.png");
        }

        var url = $"{_serverUrl!.TrimEnd('/')}/api/agent/support-request";

        try
        {
            using var response = await httpClient.PostAsync(url, form);

            var responseBody = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                string? ticketNumber = null;
                try
                {
                    using var doc = JsonDocument.Parse(responseBody);
                    if (doc.RootElement.TryGetProperty("ticket_number", out var tn))
                        ticketNumber = tn.GetString();
                    else if (doc.RootElement.TryGetProperty("ticket_id", out var ti))
                        ticketNumber = ti.GetString();
                    else if (doc.RootElement.TryGetProperty("id", out var id))
                        ticketNumber = id.GetString();
                }
                catch { }

                return (true, ticketNumber ?? "Submitted", null);
            }

            // Try to extract error message from response
            string errorMsg = $"Server returned {(int)response.StatusCode}";
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                if (doc.RootElement.TryGetProperty("error", out var err))
                    errorMsg = err.GetString() ?? errorMsg;
                else if (doc.RootElement.TryGetProperty("message", out var msg))
                    errorMsg = msg.GetString() ?? errorMsg;
            }
            catch { }

            return (false, null, errorMsg);
        }
        catch (TaskCanceledException)
        {
            return (false, null, "Request timed out");
        }
        catch (HttpRequestException ex)
        {
            return (false, null, $"Network error: {ex.Message}");
        }
    }
}
