using System.Security.AccessControl;
using System.Security.Cryptography;
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

            // Ensure config.json ACL is locked down on startup
            RestrictConfigFileAcl();

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

            // If config.json has no signing_public_key, read from registry
            // (written by MSI installer from SIGNING_PUBLIC_KEY property)
            if (string.IsNullOrEmpty(_config.SigningPublicKey))
            {
                var regPubKey = GetRegistrySigningPublicKey();
                if (regPubKey != null)
                {
                    _config.SigningPublicKey = regPubKey;
                    _logger.LogInformation("Using signing public key from registry, saving to config.json");
                    Save();
                }
                else
                {
                    _logger.LogWarning("No signing public key configured — scripts cannot be verified");
                }
            }

            // L6: Validate SigningPublicKey by attempting RSA import at load time.
            // Clears the key if it is malformed so scripts are rejected rather than
            // silently passing a broken verification path.
            if (!string.IsNullOrEmpty(_config.SigningPublicKey))
            {
                var pem = _config.SigningPublicKey.Replace("\\n", "\n");
                try
                {
                    using var rsa = RSA.Create();
                    rsa.ImportFromPem(pem);
                    _logger.LogDebug("Signing public key validated successfully (RSA import ok)");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Signing public key is malformed — clearing. Scripts cannot be verified until reinstall.");
                    _config.SigningPublicKey = null;
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
                RestrictConfigFileAcl();
                _logger.LogInformation("Config saved to {Path}", _configPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save config to {Path}", _configPath);
            }
        }
    }

    /// <summary>
    /// Restricts config.json ACL so only SYSTEM and Administrators can read it.
    /// Called after every write and on startup if the file exists with loose permissions.
    /// </summary>
    public void RestrictConfigFileAcl()
    {
        try
        {
            var fi = new FileInfo(_configPath);
            if (!fi.Exists) return;

            var security = fi.GetAccessControl();
            security.SetAccessRuleProtection(true, false); // disable inheritance
            security.AddAccessRule(new FileSystemAccessRule(
                "SYSTEM", FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                "BUILTIN\\Administrators", FileSystemRights.FullControl, AccessControlType.Allow));
            fi.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to restrict config.json ACL");
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

    private string? GetRegistrySigningPublicKey()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\CBIT\Agent");
            var value = key?.GetValue("SigningPublicKey") as string;
            if (!string.IsNullOrEmpty(value) && value != "NONE")
                return value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read signing public key from registry");
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

    /// <summary>
    /// Called after successful registration. Clears the enrollment key (customer_key) from
    /// in-memory config, config.json, and the registry — it is no longer needed once the
    /// agent has an agent_id and agent_token.
    ///
    /// Re-registration flow (on 401): The MSI writes the key back to the registry on reinstall.
    /// Call TryRefreshCustomerKey() before re-registering; if the key is gone (already consumed
    /// and agent not reinstalled), re-registration requires a fresh MSI install.
    /// </summary>
    public void ClearEnrollmentKey()
    {
        lock (_lock)
        {
            _config.CustomerKey = string.Empty;
            Save(); // Persists empty customer_key to config.json

            // Also delete from registry so the secret is not retained anywhere on disk
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\CBIT\Agent", writable: true);
                key?.DeleteValue("CustomerKey", throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete customer key from registry — continuing");
            }

            _logger.LogInformation("Registration complete — enrollment key removed from config and registry");
        }
    }

    /// <summary>
    /// Attempts to re-read the customer key from the registry. Returns true if a valid key
    /// was found and set in the in-memory config. Used before re-registration on 401 —
    /// the key will be present if the agent was recently reinstalled (MSI re-populates it).
    /// </summary>
    public bool TryRefreshCustomerKey()
    {
        lock (_lock)
        {
            var regKey = GetRegistryCustomerKey();
            if (regKey != null)
            {
                _config.CustomerKey = regKey;
                _logger.LogInformation("Customer key refreshed from registry for re-registration");
                return true;
            }
            return false;
        }
    }

    public void UpdateScreenConnectInstanceId(string? instanceId)
    {
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(instanceId) && instanceId != _config.ScreenConnectInstanceId)
            {
                _config.ScreenConnectInstanceId = instanceId;
                Save();
            }
        }
    }
}
