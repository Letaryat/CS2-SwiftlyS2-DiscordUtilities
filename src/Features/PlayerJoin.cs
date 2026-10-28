using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Players;
using DiscordUtilities.Services;

namespace DiscordUtilities;

public partial class DiscordUtilities
{
    internal void InitializePlayerJoined()
    {
        if (!Config.PlayerJoined.Enabled)
            return;

        if (string.IsNullOrWhiteSpace(Config.PlayerJoined.WebhookUrl))
        {
            Core.Logger.LogWarning(
                "[DiscordUtilities] PlayerJoined enabled but no webhook URL set"
            );

            return;
        }

        Core.Event.OnClientPutInServer += OnClientPutInServer;
        Core.Event.OnClientDisconnected += OnClientDisconnected;

        Core.Logger.LogInformation(
            "[DiscordUtilities] PlayerJoined registered"
        );
    }

    private void OnClientPutInServer(IOnClientPutInServerEvent @event)
    {
        try
        {
            var player = Core.PlayerManager.GetPlayer(@event.PlayerId);

            if (player == null)
            {
                Core.Logger.LogWarning(
                    "[DiscordUtilities] PlayerPutInServer: player not found for slot {PlayerId}",
                    @event.PlayerId
                );

                return;
            }

            if (player.IsFakeClient)
                return;

            Core.Scheduler.NextTick(() =>
            {
                _ = Task.Run(() => SendPlayerJoinedAsync(player));
            });
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(
                ex,
                "[DiscordUtilities] Failed to process player joined event"
            );
        }
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent @event)
    {
        try
        {
            var player = Core.PlayerManager.GetPlayer(@event.PlayerId);
            var playerName = player?.Name ?? "Unknown";
            var steamId = player?.SteamID ?? 0;
            var connectedTime = player?.ConnectedTime ?? 0;
            var playerSlot = @event.PlayerId;
            var reason = @event.Reason.ToString();

            if (player?.IsFakeClient == true)
                return;

            _ = Task.Run(() =>
                SendPlayerLeftAsync(
                    playerName,
                    steamId,
                    connectedTime,
                    playerSlot,
                    reason
                )
            );
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(
                ex,
                "[DiscordUtilities] Failed to process player disconnected event"
            );
        }
    }


    private async Task SendPlayerJoinedAsync(IPlayer player)
    {
        try
        {
            var config = Config.PlayerJoined;

            if (Webhook.IsOnCooldown(
                    $"player_join_{player.SteamID}",
                    config.CooldownSeconds))
            {
                return;
            }

            var serverName = GetServerDisplayName();
            var steamId = player.SteamID;

            var infoLines = new List<string>();

            if (config.ShowSteamId && steamId != 0)
            {
                infoLines.Add($"**SteamID:** `{steamId}`");
            }

            if (config.ShowPlayerCount)
            {
                infoLines.Add(
                    $"**Players Online:** `{GetOnlinePlayerCount()}`"
                );
            }

            if (config.ShowPlayerSlot)
            {
                infoLines.Add(
                    $"**Slot:** `{player.Slot}`"
                );
            }

            var playerInfo = infoLines.Count > 0
                ? string.Join("\n", infoLines)
                : "";

            var container = new DiscordComponentV2
            {
                Type = 17, // Container

                AccentColor = WebhookService.ParseColor(
                    config.JoinEmbedColor
                ),

                Components =
                [
                    new DiscordComponentV2
                {
                    Type = 9, // Section

                    Components =
                    [
                        new DiscordComponentV2
                        {
                            Type = 10, // Text Display
                            Content = "## 🟢 Player Joined"
                        },

                        new DiscordComponentV2
                        {
                            Type = 10, // Text Display
                            Content =
                                $"**{player.Name}**\n\n" +
                                playerInfo
                        }
                    ],

                    Accessory = steamId != 0
                        ? new DiscordComponentV2
                        {
                            Type = 11, // Thumbnail

                            Media = new DiscordMedia
                            {
                                Url =
                                    $"https://unavatar.io/steam/profile:{steamId}"
                            },

                            Description =
                                $"{player.Name}'s Steam avatar"
                        }
                        : null
                },

                new DiscordComponentV2
                {
                    Type = 14, // Separator
                    Divider = true,
                    Spacing = 1
                },

                new DiscordComponentV2
                {
                    Type = 1, // Action Row

                    Components =
                    [
                        new DiscordComponentV2
                        {
                            Type = 2, // Button
                            Style = 5, // Link button
                            Label = "Steam Profile",
                            Url =
                                steamId != 0
                                    ? $"https://steamcommunity.com/profiles/{steamId}"
                                    : null
                        }
                    ]
                },

                new DiscordComponentV2
                {
                    Type = 10, // Text Display
                    Content =
                        $"*{serverName} • Discord Utilities • " +
                        $"<t:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:R>*"
                }
                ]
            };

            await Webhook.SendComponentsV2Async(
                config.WebhookUrl,
                [container],
                username: serverName
            );
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(
                ex,
                "[DiscordUtilities] Failed to send player joined notification"
            );
        }
    }

    private string BuildPlayerInfo(
        IPlayer player,
        dynamic config)
    {
        var lines = new List<string>();

        if (config.ShowSteamId && player.SteamID != 0)
        {
            lines.Add($"**SteamID:** `{player.SteamID}`");
        }

        if (config.ShowPlayerCount)
        {
            lines.Add(
                $"**Players Online:** `{GetOnlinePlayerCount()}`"
            );
        }

        if (config.ShowPlayerSlot)
        {
            lines.Add($"**Slot:** `{player.Slot}`");
        }

        return string.Join("\n", lines);
    }



    private async Task SendPlayerLeftAsync(
        string playerName,
        ulong steamId,
        uint connectedTime,
        int playerSlot,
        string disconnectReason)
    {
        try
        {
            var config = Config.PlayerJoined;

            if (Webhook.IsOnCooldown(
                    $"player_leave_{steamId}_{playerSlot}",
                    config.CooldownSeconds))
            {
                return;
            }

            var serverName = GetServerDisplayName();

            var infoLines = new List<string>();

            if (config.ShowSteamId && steamId != 0)
            {
                infoLines.Add(
                    $"**SteamID:** `{steamId}`"
                );
            }

            if (config.ShowPlayerCount)
            {
                var playersOnline = Math.Max(
                    0,
                    GetOnlinePlayerCount() - 1
                );

                infoLines.Add(
                    $"**Players Online:** `{playersOnline}`"
                );
            }

            if (config.ShowPlayerSlot)
            {
                infoLines.Add(
                    $"**Slot:** `{playerSlot}`"
                );
            }

            if (config.ShowConnectionTime && connectedTime > 0)
            {
                var time = TimeSpan.FromSeconds(connectedTime);

                var duration = time.TotalHours >= 1
                    ? $"{(int)time.TotalHours}h {time.Minutes}m"
                    : time.TotalMinutes >= 1
                        ? $"{time.Minutes}m {time.Seconds}s"
                        : $"{time.Seconds}s";

                infoLines.Add(
                    $"**Session Time:** `{duration}`"
                );
            }

            if (config.ShowDisconnectReason &&
                !string.IsNullOrWhiteSpace(disconnectReason))
            {
                infoLines.Add(
                    $"**Disconnect Reason:** `{disconnectReason}`"
                );
            }

            var playerInfo = string.Join(
                "\n",
                infoLines
            );

            var container = new DiscordComponentV2
            {
                Type = 17, // Container

                AccentColor = WebhookService.ParseColor(
                    config.LeaveEmbedColor
                ),

                Components =
                [
                    new DiscordComponentV2
                {
                    Type = 9, // Section

                    Components =
                    [
                        new DiscordComponentV2
                        {
                            Type = 10, // Text Display
                            Content = "## 🔴 Player Left"
                        },

                        new DiscordComponentV2
                        {
                            Type = 10, // Text Display
                            Content =
                                $"**{playerName}**\n\n" +
                                playerInfo
                        }
                    ],

                    Accessory = steamId != 0
                        ? new DiscordComponentV2
                        {
                            Type = 11, // Thumbnail

                            Media = new DiscordMedia
                            {
                                Url =
                                    $"https://unavatar.io/steam/profile:{steamId}"
                            },

                            Description =
                                $"{playerName}'s Steam avatar"
                        }
                        : null
                },

                new DiscordComponentV2
                {
                    Type = 14, // Separator
                    Divider = true,
                    Spacing = 1
                },

                new DiscordComponentV2
                {
                    Type = 1, // Action Row

                    Components =
                    [
                        new DiscordComponentV2
                        {
                            Type = 2, // Button
                            Style = 5,
                            Label = "Steam Profile",
                            Url =
                                steamId != 0
                                    ? $"https://steamcommunity.com/profiles/{steamId}"
                                    : null
                        }
                    ]
                },

                new DiscordComponentV2
                {
                    Type = 10, // Text Display
                    Content =
                        $"*{serverName} • Discord Utilities • " +
                        $"<t:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}:R>*"
                }
                ]
            };

            await Webhook.SendComponentsV2Async(
                config.WebhookUrl,
                [container],
                username: serverName
            );
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(
                ex,
                "[DiscordUtilities] Failed to send player left notification"
            );
        }
    }

}