using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Misc;
using DiscordUtilities.Services;

namespace DiscordUtilities;

public partial class DiscordUtilities
{
    private readonly ConcurrentDictionary<ulong, DateTime> _chatCooldowns = new();
    private Guid _chatHookGuid = Guid.Empty;

    internal void InitializeChatRelay()
    {
        if (!Config.ChatRelay.Enabled) return;

        if (string.IsNullOrWhiteSpace(Config.ChatRelay.WebhookUrl))
        {
            Core.Logger.LogWarning("[DiscordUtilities] ChatRelay enabled but no webhook URL set");
            return;
        }

        if (_chatHookGuid != Guid.Empty)
        {
            Core.Command.UnhookClientChat(_chatHookGuid);
            _chatHookGuid = Guid.Empty;
        }

        _chatHookGuid = Core.Command.HookClientChat((playerId, text, teamonly) =>
        {
            _ = Task.Run(() => OnPlayerChatAsync(playerId, text, teamonly));
            return HookResult.Continue;
        });

        Core.Logger.LogInformation("[DiscordUtilities] ChatRelay registered via HookClientChat");
    }

    private async Task OnPlayerChatAsync(int playerId, string text, bool teamOnly)
    {
        try
        {
            var config = Config.ChatRelay;

            if (string.IsNullOrWhiteSpace(text))
                return;

            if (teamOnly && config.IgnoreTeamChat)
                return;

            var trimmedText = text.Trim();

            if (config.IgnoreCommands)
            {
                if (trimmedText.StartsWith('!') || trimmedText.StartsWith('/'))
                    return;

                if (config.IgnoredPrefixes != null && config.IgnoredPrefixes.Count > 0)
                {
                    foreach (var prefix in config.IgnoredPrefixes)
                    {
                        if (string.IsNullOrWhiteSpace(prefix))
                            continue;

                        if (trimmedText.StartsWith(
                                prefix.Trim(),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }
                    }
                }
            }

            var player = Core.PlayerManager.GetPlayer(playerId);

            var playerName = player?.Name ?? $"Player #{playerId}";
            ulong steamId64 = player?.SteamID ?? 0;

            if (config.CooldownSeconds > 0 && steamId64 != 0)
            {
                var now = DateTime.UtcNow;

                if (_chatCooldowns.TryGetValue(steamId64, out var lastMsg) &&
                    (now - lastMsg).TotalSeconds < config.CooldownSeconds)
                {
                    return;
                }

                _chatCooldowns[steamId64] = now;
            }

            var steamId2 = ConvertSteamId64ToSteamId2(steamId64);

            var format = teamOnly
                ? (string.IsNullOrWhiteSpace(config.TeamMessageFormat)
                    ? "(TEAM) {Message}"
                    : config.TeamMessageFormat)
                : (string.IsNullOrWhiteSpace(config.MessageFormat)
                    ? "{Message}"
                    : config.MessageFormat);

            var formattedMessage = format
                .Replace("{PlayerName}", playerName)
                .Replace("{SteamId2}", steamId2)
                .Replace("{SteamId64}", steamId64.ToString())
                .Replace("{Message}", text);

            string? avatarUrl = null;

            if (config.UseSteamAvatars && steamId64 != 0)
            {
                // WAŻNE: tutaj nie ma '$' przed SteamID
                avatarUrl = $"https://unavatar.io/steam/profile:{steamId64}";
            }

            var components = new List<DiscordComponentV2>();

            // ============================================================
            // CONTAINER
            // ============================================================

            var container = new DiscordComponentV2
            {
                Type = 17,
                Components = []
            };

            // ============================================================
            // PLAYER HEADER
            // ============================================================

            var playerHeader = new DiscordComponentV2
            {
                Type = 9, // SECTION
                Components =
                [
                    new DiscordComponentV2
                {
                    Type = 10, // TEXT_DISPLAY
                    Content =
                        $"### {EscapeMarkdown(playerName)}\n" +
                        $"`{EscapeMarkdown(steamId2)}`"
                }
                ]
            };

            // Thumbnail jest accessory SECTION.
            // Nie dodajemy go, jeśli nie mamy poprawnego SteamID.
            if (!string.IsNullOrWhiteSpace(avatarUrl))
            {
                playerHeader.Accessory = new DiscordComponentV2
                {
                    Type = 11, // THUMBNAIL
                    Media = new DiscordMedia
                    {
                        Url = avatarUrl
                    }
                };
            }

            container.Components.Add(playerHeader);

            // ============================================================
            // SEPARATOR
            // ============================================================

            container.Components.Add(
                new DiscordComponentV2
                {
                    Type = 14, // SEPARATOR
                    Divider = true,
                    Spacing = 1
                });

            // ============================================================
            // MESSAGE
            // ============================================================

            container.Components.Add(
                new DiscordComponentV2
                {
                    Type = 10, // TEXT_DISPLAY
                    Content = EscapeMarkdown(formattedMessage)
                });

            // ============================================================
            // STEAM BUTTON
            // ============================================================

            if (steamId64 != 0)
            {
                container.Components.Add(
                    new DiscordComponentV2
                    {
                        Type = 1, // ACTION_ROW
                        Components =
                        [
                            new DiscordComponentV2
                        {
                            Type = 2, // BUTTON
                            Style = 5, // LINK
                            Label = "Steam Profile",
                            Url = $"https://steamcommunity.com/profiles/{steamId64}"
                        }
                        ]
                    });
            }

            components.Add(container);

            // ============================================================
            // SEND
            // ============================================================

            await Webhook.SendComponentsV2Async(
                config.WebhookUrl,
                components);
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(
                ex,
                "[DiscordUtilities] ChatRelay failed");
        }
    }

    private static string ConvertSteamId64ToSteamId2(ulong steamId64)
    {
        if (steamId64 < 76561197960265728)
            return steamId64.ToString();

        var accountId = steamId64 - 76561197960265728;
        var y = accountId % 2;
        var z = accountId / 2;

        return $"STEAM_0:{y}:{z}";
    }

    private static string EscapeMarkdown(string text)
    {
        return text
            .Replace("\\", "\\\\")
            .Replace("*", "\\*")
            .Replace("_", "\\_")
            .Replace("~", "\\~")
            .Replace("`", "\\`")
            .Replace(">", "\\>")
            .Replace("|", "\\|");
    }

}
