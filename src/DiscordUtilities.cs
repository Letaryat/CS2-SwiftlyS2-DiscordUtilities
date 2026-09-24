using System.IO;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Plugins;
using SwiftlyS2.Shared;
using DiscordUtilities.Config;
using DiscordUtilities.Services;

namespace DiscordUtilities;

[PluginMetadata
    (
        Id = "DiscordUtilities",
        Version = "1.1.2",
        Name = "Discord Utilities",
        Author = "SyntX34",
        Description = "Discord integration for CS2: map notifications, chat relay, admin logs, calladmin, bugreport."
    )
]
public partial class DiscordUtilities : BasePlugin
{
    internal WebhookService Webhook { get; private set; } = null!;
    internal SteamApiService SteamApi { get; private set; } = null!;
    internal DiscordBotService? DiscordBot { get; private set; }
    internal PluginConfig Config { get; private set; } = new();

    private FileSystemWatcher? _configWatcher;

    public DiscordUtilities(ISwiftlyCore core) : base(core) { }

    public override void ConfigureSharedInterface(IInterfaceManager interfaceManager) { }
    public override void UseSharedInterface(IInterfaceManager interfaceManager) { }

    public override void Load(bool hotReload)
    {
        Core.Configuration
            .InitializeJsonWithModel<PluginConfig>("config.jsonc", "DiscordUtilities")
            .InitializeWithTemplate("calladmin_reasons.jsonc", "calladmin_reasons.jsonc")
            .InitializeWithTemplate("bug_reasons.jsonc", "bug_reasons.jsonc");

        Webhook = new WebhookService(Core.Logger);
        SteamApi = new SteamApiService(Core.Logger);

        LoadPluginConfig();
        SetupConfigFileWatcher();

        InitializeMapNotification();
        InitializeChatRelay();
        InitializeAdminLogs();
        InitializeCallAdmin();
        InitializeBugReport();
        InitializeDiscordToServer();

        Core.Logger.LogInformation("[DiscordUtilities] Loaded — Map: {Map} | Chat: {Chat} | Admin: {Admin} | CallAdmin: {CallAdmin} | BugReport: {BugReport} | DiscordToServer: {Bot}",
            Config.MapNotification.Enabled, Config.ChatRelay.Enabled, Config.AdminLogs.Enabled, Config.CallAdmin.Enabled, Config.BugReport.Enabled, Config.DiscordToServer.Enabled);
    }

    private void InitializeDiscordToServer()
    {
        if (Config.DiscordToServer.Enabled &&
            !string.IsNullOrWhiteSpace(Config.DiscordToServer.BotToken) &&
            !string.IsNullOrWhiteSpace(Config.DiscordToServer.ChannelId))
        {
            DiscordBot?.Dispose();
            DiscordBot = new DiscordBotService(Core.Logger, OnDiscordMessageReceived, () => Config.Debug);
            DiscordBot.Start(Config.DiscordToServer.BotToken, Config.DiscordToServer.ChannelId);
            if (Config.Debug)
                Core.Logger.LogInformation("[DiscordUtilities] DiscordToServer bot service started for channel {Channel}", Config.DiscordToServer.ChannelId);
        }
        else
        {
            DiscordBot?.Stop();
        }
    }

    private void OnDiscordMessageReceived(string authorName, string message)
    {
        try
        {
            var config = Config.DiscordToServer;
            var formatted = config.TagFormat
                .Replace("{ChatPrefix}", config.ChatPrefix)
                .Replace("{DiscordName}", authorName)
                .Replace("{Message}", message);

            Core.Scheduler.NextTick(() =>
            {
                try
                {
                    Core.PlayerManager.SendChat(formatted);
                }
                catch
                {
                    foreach (var p in Core.PlayerManager.GetAllValidPlayers())
                    {
                        if (p.IsValid)
                            p.SendChat(formatted);
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(ex, "[DiscordUtilities] Failed to broadcast Discord message to server");
        }
    }

    private void LoadPluginConfig()
    {
        try
        {
            var configPath = Core.Configuration.GetConfigPath("config.jsonc");
            if (File.Exists(configPath))
            {
                var content = File.ReadAllText(configPath);
                var options = new JsonSerializerOptions
                {
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                    PropertyNameCaseInsensitive = true
                };

                using var doc = JsonDocument.Parse(content, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

                if (doc.RootElement.TryGetProperty("DiscordUtilities", out var section))
                {
                    Config = JsonSerializer.Deserialize<PluginConfig>(section.GetRawText(), options) ?? new PluginConfig();
                }
                else
                {
                    Config = JsonSerializer.Deserialize<PluginConfig>(content, options) ?? new PluginConfig();
                }

                SteamApi?.SetApiKey(Config.SteamApiKey);
                _ = ValidateConfiguredEndpointsAsync();
                Core.Logger.LogInformation("[DiscordUtilities] Configuration loaded successfully from {Path}", configPath);
            }
            else
            {
                Core.Logger.LogWarning("[DiscordUtilities] Config file not found at {Path}", configPath);
            }
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(ex, "[DiscordUtilities] Failed to load config.jsonc");
        }
    }

    private void SetupConfigFileWatcher()
    {
        try
        {
            var configPath = Core.Configuration.GetConfigPath("config.jsonc");
            var directory = Path.GetDirectoryName(configPath);
            if (directory != null && Directory.Exists(directory))
            {
                _configWatcher = new FileSystemWatcher(directory, "config.jsonc")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                _configWatcher.Changed += (s, e) =>
                {
                    Task.Delay(500).ContinueWith(_ =>
                    {
                        LoadPluginConfig();
                        InitializeDiscordToServer();
                        Core.Logger.LogInformation("[DiscordUtilities] Configuration hot-reloaded from disk.");
                    });
                };
            }
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(ex, "[DiscordUtilities] Failed to setup config file watcher");
        }
    }

    private async Task ValidateConfiguredEndpointsAsync()
    {
        if (Config.MapNotification.Enabled && !string.IsNullOrWhiteSpace(Config.MapNotification.WebhookUrl))
        {
            var valid = await Webhook.ValidateWebhookAsync(Config.MapNotification.WebhookUrl);
            if (!valid)
                Core.Logger.LogWarning("[DiscordUtilities] MapNotification Webhook URL appears invalid or unreachable!");
        }

        if (Config.ChatRelay.Enabled && !string.IsNullOrWhiteSpace(Config.ChatRelay.WebhookUrl))
        {
            var valid = await Webhook.ValidateWebhookAsync(Config.ChatRelay.WebhookUrl);
            if (!valid)
                Core.Logger.LogWarning("[DiscordUtilities] ChatRelay Webhook URL appears invalid or unreachable!");
        }

        if (Config.AdminLogs.Enabled && !string.IsNullOrWhiteSpace(Config.AdminLogs.WebhookUrl))
        {
            var valid = await Webhook.ValidateWebhookAsync(Config.AdminLogs.WebhookUrl);
            if (!valid)
                Core.Logger.LogWarning("[DiscordUtilities] AdminLogs Webhook URL appears invalid or unreachable!");
        }

        if (Config.CallAdmin.Enabled && !string.IsNullOrWhiteSpace(Config.CallAdmin.WebhookUrl))
        {
            var valid = await Webhook.ValidateWebhookAsync(Config.CallAdmin.WebhookUrl);
            if (!valid)
                Core.Logger.LogWarning("[DiscordUtilities] CallAdmin Webhook URL appears invalid or unreachable!");
        }

        if (Config.BugReport.Enabled && !string.IsNullOrWhiteSpace(Config.BugReport.WebhookUrl))
        {
            var valid = await Webhook.ValidateWebhookAsync(Config.BugReport.WebhookUrl);
            if (!valid)
                Core.Logger.LogWarning("[DiscordUtilities] BugReport Webhook URL appears invalid or unreachable!");
        }

        if (Config.DiscordToServer.Enabled)
        {
            if (string.IsNullOrWhiteSpace(Config.DiscordToServer.BotToken))
            {
                Core.Logger.LogWarning("[DiscordUtilities] DiscordToServer is enabled but BotToken is missing!");
            }
            else
            {
                var (isValid, botName, error) = await DiscordBotService.ValidateBotTokenAsync(Config.DiscordToServer.BotToken);
                if (isValid)
                {
                    if (Config.Debug)
                        Core.Logger.LogInformation("[DiscordUtilities] Discord Bot Token verified successfully (Bot: {BotName})", botName);
                }
                else
                {
                    Core.Logger.LogWarning("[DiscordUtilities] Discord Bot Token validation failed: {Reason}", error);
                }
            }

            if (string.IsNullOrWhiteSpace(Config.DiscordToServer.ChannelId))
                Core.Logger.LogWarning("[DiscordUtilities] DiscordToServer is enabled but ChannelId is missing!");
        }
    }

    internal string GetServerDisplayName()
    {
        if (!string.IsNullOrWhiteSpace(Config.ServerName))
            return Config.ServerName;

        try
        {
            var hostname = Core.ConVar.FindAsString("hostname")?.ValueAsString;
            if (!string.IsNullOrWhiteSpace(hostname))
                return hostname;
        }
        catch { }

        return "CS2 Server";
    }

    internal string GetServerConnectAddress()
    {
        if (!string.IsNullOrWhiteSpace(Config.ServerDNS))
            return Config.ServerDNS;

        if (!string.IsNullOrWhiteSpace(Config.ServerIP))
            return Config.ServerIP;

        try
        {
            var ip = Core.ConVar.FindAsString("ip")?.ValueAsString;
            var port = Core.ConVar.FindAsString("hostport")?.ValueAsString ?? "27015";

            if (!string.IsNullOrWhiteSpace(ip) && ip != "0.0.0.0" && ip != "localhost" && ip != "127.0.0.1")
                return $"{ip}:{port}";
        }
        catch { }

        return string.Empty;
    }

    public override void Unload()
    {
        _configWatcher?.Dispose();
        DiscordBot?.Dispose();
        Webhook?.Dispose();
        SteamApi?.Dispose();
        Core.Logger.LogInformation("[DiscordUtilities] Unloaded");
    }
}