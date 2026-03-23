using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CbitAgent.Configuration;

public class ConfigManager
{
    private readonly string _configPath;
    private readonly ILogger<ConfigManager> _logger;
    private readonly object _lock = new();
    private AgentConfig _config = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public ConfigManager(ILogger<ConfigManager> logger)
    {
        _logger = logger;
        var exeDir = AppContext.BaseDirectory;
        _configPath = Path.Combine(exeDir, "config.json");
    }

    public AgentConfig Config => _config;

    public AgentConfig Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_configPath))
            {
                _logger.LogWarning("Config file not found at {Path}, using defaults", _configPath);
                _config = new AgentConfig();
            }
            else
            {
                try
                {
                    var json = File.ReadAllText(_configPath);
                    _config = JsonSerializer.Deserialize<AgentConfig>(json, JsonOptions) ?? new AgentConfig();
                    _logger.LogInformation("Config loaded from {Path}", _configPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load config from {Path}", _configPath);
                    _config = new AgentConfig();
                }
            }

            // If config.json has no customer_key, read from registry
            // (written by MSI installer from CUSTOMER_KEY property)
            if (string.IsNullOrEmpty(_config.CustomerKey))
            {
                var regKey = GetRegistryCustomerKey();
                if (regKey != null)
                {
                    _config.CustomerKey = regKey;
                    _logger.LogInformation("Using customer key from registry, saving to config.json");
                    Save(); // Persist to config.json so registry is only needed once
                }
                else
                {
                    _logger.LogError("No customer key configured — reinstall agent with CUSTOMER_KEY property");
                }
            }

            return _config;
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(_config, JsonOptions);
                File.WriteAllText(_configPath, json);
                _logger.LogInformation("Config saved to {Path}", _configPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save config to {Path}", _configPath);
            }
        }
    }

    private string? GetRegistryCustomerKey()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\CBIT\Agent");
            var value = key?.GetValue("CustomerKey") as string;
            if (!string.IsNullOrEmpty(value) && value != "CBIT_YOURKEY")
                return value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read customer key from registry");
        }
        return null;
    }

    public void UpdateRegistration(string agentId, string agentToken, int checkInInterval)
    {
        lock (_lock)
        {
            _config.AgentId = agentId;
            _config.AgentToken = agentToken;
            _config.CheckInIntervalMinutes = checkInInterval;
            Save();
        }
    }
}
