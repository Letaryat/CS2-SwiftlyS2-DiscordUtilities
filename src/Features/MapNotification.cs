using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.GameEventDefinitions;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.GameEvents;
using DiscordUtilities.Services;

namespace DiscordUtilities;

public partial class DiscordUtilities
{
    private string _currentMapName = "Unknown";
    private string _lastNotifiedMap = "";

    internal void InitializeMapNotification()
    {
        if (!Config.MapNotification.Enabled) return;

        if (string.IsNullOrWhiteSpace(Config.MapNotification.WebhookUrl))
        {
            Core.Logger.LogWarning("[DiscordUtilities] MapNotification enabled but no webhook URL set");
            return;
        }

        Core.Event.OnMapLoad += (@event) =>
        {
            if (!string.IsNullOrWhiteSpace(@event.MapName))
            {
                var newMap = @event.MapName;
                var isDifferentMap = !string.Equals(_lastNotifiedMap, newMap, StringComparison.OrdinalIgnoreCase);

                _currentMapName = newMap;

                if (isDifferentMap)
                {
                    _ = Task.Run(() => OnMapLoadedAsync(isExtension: false));
                }
            }
        };

        // Hook map extension commands / chat
        Core.Command.HookClientChat((playerId, text, teamonly) =>
        {
            if (string.IsNullOrWhiteSpace(text)) return HookResult.Continue;
            var trimmed = text.Trim().ToLowerInvariant();
            if (trimmed is "!extend" or "/extend" or "!ext" or "/ext" or "!mapextend" or "/mapextend")
            {
                _ = Task.Run(() => OnMapLoadedAsync(isExtension: true));
            }
            return HookResult.Continue;
        });

        Core.Logger.LogInformation("[DiscordUtilities] MapNotification registered");
    }

    private async Task OnMapLoadedAsync(bool isExtension = false)
    {
        try
        {
            var config = Config.MapNotification;

            if (Webhook.IsOnCooldown("map_notification", config.CooldownSeconds))
                return;

            var mapName = _currentMapName;
            if (string.IsNullOrWhiteSpace(mapName) || mapName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                var mapConvar = Core.ConVar.FindAsString("mapname");
                if (!string.IsNullOrWhiteSpace(mapConvar?.ValueAsString))
                    mapName = mapConvar.ValueAsString;
            }

            if (!isExtension && string.Equals(_lastNotifiedMap, mapName, StringComparison.OrdinalIgnoreCase))
                return;

            _lastNotifiedMap = mapName;

            var (isWorkshop, workshopId) = SteamApiService.TryParseWorkshopId(mapName);

            var cleanMapName = mapName;
            var lastSlash = mapName.LastIndexOfAny(['/', '\\']);
            if (lastSlash >= 0 && lastSlash < mapName.Length - 1)
                cleanMapName = mapName[(lastSlash + 1)..];

            var playerCount = GetOnlinePlayerCount();
            var serverName = GetServerDisplayName();
            var connectAddress = GetServerConnectAddress();

            var titlePrefix = isExtension ? "⏳ Map Extended" : "🗺️ Map Notification";

            var embed = new DiscordEmbed
            {
                Title = titlePrefix,
                Color = WebhookService.ParseColor(config.EmbedColor),
                Footer = new EmbedFooter { Text = $"{serverName} • Discord Utilities" }
            };
            embed.WithTimestamp();

            string? mapImageUrl = null;
            WorkshopMapInfo? workshopInfo = null;

            if (isWorkshop)
            {
                workshopInfo = await SteamApi.GetWorkshopMapInfoAsync(workshopId);
            }

            if (isWorkshop && config.ShowWorkshopId)
            {
                if (workshopInfo != null)
                {
                    embed.Title = isExtension ? $"⏳ Map Extended: **{workshopInfo.Title}**" : $"🗺️ Map: **{workshopInfo.Title}**";
                    embed.Url = workshopInfo.WorkshopUrl;
                    embed.AddField("Map Name", $"`{cleanMapName}`", true);
                    embed.AddField("Workshop ID", $"[{workshopId}]({workshopInfo.WorkshopUrl})", true);
                }
                else
                {
                    embed.AddField("Map Name", $"`{cleanMapName}`", true);
                    embed.AddField("Workshop ID", workshopId.ToString(), true);
                }

                mapImageUrl = await SteamApi.GetMapImageAsync(cleanMapName, workshopId);
            }
            else
            {
                embed.AddField("Current Map", $"`{cleanMapName}`", true);
                mapImageUrl = await SteamApi.GetMapImageAsync(cleanMapName, isWorkshop ? workshopId : null);
            }

            if (config.ShowPlayerCount)
                embed.AddField("Players Online", $"{playerCount}", true);

            var isHttpConnect = !string.IsNullOrWhiteSpace(connectAddress) &&
                                (connectAddress.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                 connectAddress.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

            if (config.ShowServerIP && !string.IsNullOrWhiteSpace(connectAddress))
            {
                if (isHttpConnect)
                {
                    embed.AddField("Quick Connect", $"[👉 Click Here to Connect]({connectAddress})", false);
                }
                else
                {
                    embed.AddField("Quick Connect", $"`connect {connectAddress}`", false);
                }
            }

            // Thumbnail (Server / Community Banner logo)
            if (!string.IsNullOrWhiteSpace(config.BannerUrl))
            {
                embed.Thumbnail = new EmbedImage { Url = config.BannerUrl };
            }

            // Main Image (Map Image)
            if (!string.IsNullOrWhiteSpace(mapImageUrl))
            {
                embed.Image = new EmbedImage { Url = mapImageUrl };
            }

            // Action Row Buttons (Connect Now & Steam Workshop Link)
            List<DiscordComponentActionRow>? components = null;
            var buttons = new List<DiscordComponentButton>();

            if (!string.IsNullOrWhiteSpace(connectAddress))
            {
                string buttonUrl;

                if (isHttpConnect)
                {
                    buttonUrl = connectAddress;
                }
                else
                {
                    var target = connectAddress.Replace("connect ", "", StringComparison.OrdinalIgnoreCase).Trim();
                    buttonUrl = $"https://vauff.com/connect.php?ip={target}";
                }

                buttons.Add(new DiscordComponentButton
                {
                    Label = "Connect Now",
                    Url = buttonUrl,
                    Emoji = new DiscordEmoji { Name = "🎮" }
                });
            }

            if (isWorkshop && workshopId > 0 && config.ShowWorkshopId)
            {
                var wsUrl = workshopInfo?.WorkshopUrl ?? $"https://steamcommunity.com/sharedfiles/filedetails/?id={workshopId}";
                buttons.Add(new DiscordComponentButton
                {
                    Label = "Workshop Page",
                    Url = wsUrl,
                    Emoji = new DiscordEmoji { Name = "🗺️" }
                });
            }

            if (buttons.Count > 0)
            {
                components =
                [
                    new DiscordComponentActionRow
                    {
                        Components = buttons
                    }
                ];
            }

            await Webhook.SendEmbedAsync(config.WebhookUrl, embed, username: serverName, components: components);
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(ex, "[DiscordUtilities] MapNotification failed");
        }
    }

    private int GetOnlinePlayerCount()
    {
        try
        {
            return Core.PlayerManager.GetAllValidPlayers().Count();
        }
        catch
        {
            return 0;
        }
    }
}
