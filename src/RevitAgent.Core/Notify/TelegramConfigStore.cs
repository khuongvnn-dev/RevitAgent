using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RevitAgent.Core.Audit;
using RevitAgent.Core.Tools;

namespace RevitAgent.Core.Notify;

/// <summary>
/// Plain JSON representation saved in <c>config/telegram.json</c>. NEVER contains the secret token.
/// </summary>
public sealed class TelegramConfigFile
{
    [JsonPropertyName("chat_id")]
    public long ChatId { get; set; }

    [JsonPropertyName("timeout")]
    public int Timeout { get; set; } = 30;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

/// <summary>
/// Manages loading and saving Telegram configuration:
/// - Plain settings (chat_id, timeout, enabled) in <c>%REVITAGENT_HOME%\config\telegram.json</c>.
/// - Token encrypted with DPAPI CurrentUser in <c>%REVITAGENT_HOME%\config\telegram.token.dat</c>.
/// </summary>
public sealed class TelegramConfigStore
{
    public const string JsonFileName = "telegram.json";
    public const string TokenFileName = "telegram.token.dat";

    private readonly AgentPaths _paths;
    private readonly AuditLog? _auditLog;
    private readonly object _gate = new();

    public TelegramConfigStore(AgentPaths paths, AuditLog? auditLog = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _auditLog = auditLog;
    }

    public string JsonPath => Path.Combine(_paths.ConfigDir, JsonFileName);
    public string TokenPath => Path.Combine(_paths.ConfigDir, TokenFileName);

    public TelegramOptions Load()
    {
        lock (_gate)
        {
            var options = new TelegramOptions();

            // Load plain settings
            try
            {
                if (File.Exists(JsonPath))
                {
                    var json = File.ReadAllText(JsonPath, Encoding.UTF8);
                    var cfg = JsonSerializer.Deserialize<TelegramConfigFile>(json, GatewayJson.Options);
                    if (cfg != null)
                    {
                        options.ChatId = cfg.ChatId;
                        options.AskTimeoutMinutes = cfg.Timeout > 0 ? cfg.Timeout : 30;
                        options.Enabled = cfg.Enabled;
                    }
                }
            }
            catch (Exception ex)
            {
                _auditLog?.Diagnostic($"Failed to load Telegram JSON config from '{JsonPath}'", ex);
            }

            // Load DPAPI-encrypted bot token
            try
            {
                if (File.Exists(TokenPath))
                {
                    var encryptedBytes = File.ReadAllBytes(TokenPath);
                    if (encryptedBytes.Length > 0)
                    {
                        var plainBytes = ProtectedData.Unprotect(encryptedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
                        options.BotToken = Encoding.UTF8.GetString(plainBytes).Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                _auditLog?.Diagnostic($"Failed to decrypt Telegram token from '{TokenPath}'", ex);
            }

            return options;
        }
    }

    public void Save(TelegramOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (_gate)
        {
            Directory.CreateDirectory(_paths.ConfigDir);

            // 1. Save plain config (chat_id, timeout, enabled) - NEVER the token!
            var plainConfig = new TelegramConfigFile
            {
                ChatId = options.ChatId,
                Timeout = options.AskTimeoutMinutes > 0 ? options.AskTimeoutMinutes : 30,
                Enabled = options.Enabled
            };

            var json = JsonSerializer.Serialize(plainConfig, GatewayJson.Options);
            File.WriteAllText(JsonPath, json, new UTF8Encoding(false));

            // 2. Save encrypted bot token using DPAPI CurrentUser
            if (!string.IsNullOrWhiteSpace(options.BotToken))
            {
                var plainBytes = Encoding.UTF8.GetBytes(options.BotToken.Trim());
                var encryptedBytes = ProtectedData.Protect(plainBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(TokenPath, encryptedBytes);
            }
            else if (File.Exists(TokenPath))
            {
                try
                {
                    File.Delete(TokenPath);
                }
                catch (IOException)
                {
                    // Best effort
                }
            }
        }
    }
}
