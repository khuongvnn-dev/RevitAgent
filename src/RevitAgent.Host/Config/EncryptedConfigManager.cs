using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RevitAgent.Host.Config;

/// <summary>
/// Manages encrypted persistence of <see cref="AppConfig"/> using Windows DPAPI (Data Protection API).
/// </summary>
public sealed class EncryptedConfigManager
{
    private static readonly byte[] EntropySalt = Encoding.UTF8.GetBytes("RevitAgent::DPAPI::EntropySalt::v1");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string ConfigFilePath { get; }

    public bool ConfigFileExists => File.Exists(ConfigFilePath);

    public EncryptedConfigManager(string? customFilePath = null)
    {
        ConfigFilePath = !string.IsNullOrWhiteSpace(customFilePath)
            ? customFilePath
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RevitAgent",
                "appsettings.enc");
    }

    /// <summary>
    /// Encrypts and writes the configuration to disk.
    /// </summary>
    public void SaveConfig(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var directory = Path.GetDirectoryName(ConfigFilePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Keep every field this version does not know (written by v1 or by a newer version): nothing in the user's
        // configuration is ever dropped by a save. Live finding 2026-11-25: the v1 Telegram token was lost because
        // the v2 Host re-serialized only its own AppConfig fields.
        var node = JsonSerializer.SerializeToNode(config, JsonOptions)!.AsObject();
        if (TryReadExistingJson() is { } existing)
        {
            foreach (var (key, value) in existing)
            {
                if (!node.ContainsKey(key))
                {
                    node[key] = value?.DeepClone();
                }
            }
        }

        string json = node.ToJsonString(JsonOptions);
        byte[] plainBytes = Encoding.UTF8.GetBytes(json);
        byte[] encryptedBytes = ProtectedData.Protect(plainBytes, EntropySalt, DataProtectionScope.CurrentUser);

        // Atomic write pattern: write to temporary file then replace
        string tempFile = Path.Combine(
            directory ?? Path.GetTempPath(),
            $"{Path.GetFileName(ConfigFilePath)}.{Guid.NewGuid():N}.tmp");

        File.WriteAllBytes(tempFile, encryptedBytes);
        File.Move(tempFile, ConfigFilePath, overwrite: true);
    }

    /// <summary>The current file as a JSON object, or null when it is missing or cannot be decrypted/parsed.</summary>
    private System.Text.Json.Nodes.JsonObject? TryReadExistingJson()
    {
        try
        {
            if (!File.Exists(ConfigFilePath))
            {
                return null;
            }

            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(ConfigFilePath), EntropySalt, DataProtectionScope.CurrentUser);
            return System.Text.Json.Nodes.JsonNode.Parse(Encoding.UTF8.GetString(plain)) as System.Text.Json.Nodes.JsonObject;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Loads and decrypts the configuration from disk. If file does not exist, returns default configuration.
    /// </summary>
    /// <exception cref="CryptographicException">Thrown if DPAPI decryption fails or payload is corrupted.</exception>
    public AppConfig LoadConfig()
    {
        if (!File.Exists(ConfigFilePath))
        {
            return new AppConfig();
        }

        byte[] encryptedBytes = File.ReadAllBytes(ConfigFilePath);
        byte[] plainBytes;

        try
        {
            plainBytes = ProtectedData.Unprotect(encryptedBytes, EntropySalt, DataProtectionScope.CurrentUser);
        }
        catch (Exception ex) when (ex is not CryptographicException)
        {
            throw new CryptographicException("Failed to decrypt configuration file.", ex);
        }

        try
        {
            string json = Encoding.UTF8.GetString(plainBytes);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (config == null)
            {
                throw new CryptographicException("Deserialized configuration payload is null.");
            }
            return config;
        }
        catch (JsonException ex)
        {
            throw new CryptographicException("Configuration file payload is corrupted or invalid JSON.", ex);
        }
    }
}
