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

            var embed = new DiscordEmbed
            {
                Title = "🟢 Player Joined",
                Color = WebhookService.ParseColor(config.JoinEmbedColor),
                Footer = new EmbedFooter
                {
                    Text = $"{serverName} • Discord Utilities"
                }
            };

            embed.WithTimestamp();

            embed.AddField(
                "Player",
                $"**{player.Name}**",
                true
            );

            if (config.ShowSteamId && player.SteamID != 0)
            {
                embed.AddField(
                    "SteamID",
                    $"`{player.SteamID}`",
                    true
                );
            }

            if (config.ShowPlayerCount)
            {
                embed.AddField(
                    "Players Online",
                    $"{GetOnlinePlayerCount()}",
                    true
                );
            }

            if (config.ShowPlayerSlot)
            {
                embed.AddField(
                    "Slot",
                    $"`{player.Slot}`",
                    true
                );
            }

            if (config.ShowSteamProfile && player.SteamID != 0)
            {
                embed.AddField(
                    "Steam Profile",
                    $"[View Profile](https://steamcommunity.com/profiles/{player.SteamID})",
                    false
                );
            }

            await Webhook.SendEmbedAsync(
                config.WebhookUrl,
                embed,
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

            var embed = new DiscordEmbed
            {
                Title = "🔴 Player Left",
                Color = WebhookService.ParseColor(config.LeaveEmbedColor),
                Footer = new EmbedFooter
                {
                    Text = $"{serverName} • Discord Utilities"
                }
            };

            embed.WithTimestamp();

            embed.AddField(
                "Player",
                $"**{playerName}**",
                true
            );

            if (config.ShowSteamId && steamId != 0)
            {
                embed.AddField(
                    "SteamID",
                    $"`{steamId}`",
                    true
                );
            }

            if (config.ShowPlayerCount)
            {
                var playersOnline = Math.Max(
                    0,
                    GetOnlinePlayerCount() - 1
                );

                embed.AddField(
                    "Players Online",
                    $"{playersOnline}",
                    true
                );
            }

            if (config.ShowPlayerSlot)
            {
                embed.AddField(
                    "Slot",
                    $"`{playerSlot}`",
                    true
                );
            }

            if (config.ShowConnectionTime && connectedTime > 0)
            {
                var time = TimeSpan.FromSeconds(connectedTime);

                string duration;

                if (time.TotalHours >= 1)
                {
                    duration =
                        $"{(int)time.TotalHours}h {time.Minutes}m";
                }
                else if (time.TotalMinutes >= 1)
                {
                    duration =
                        $"{time.Minutes}m {time.Seconds}s";
                }
                else
                {
                    duration =
                        $"{time.Seconds}s";
                }

                embed.AddField(
                    "Session Time",
                    $"`{duration}`",
                    true
                );
            }
            if (config.ShowDisconnectReason &&
                !string.IsNullOrWhiteSpace(disconnectReason))
            {
                embed.AddField(
                    "Disconnect Reason",
                    $"`{disconnectReason}`",
                    false
                );
            }

            if (config.ShowSteamProfile && steamId != 0)
            {
                embed.AddField(
                    "Steam Profile",
                    $"[View Profile](https://steamcommunity.com/profiles/{steamId})",
                    false
                );
            }

            await Webhook.SendEmbedAsync(
                config.WebhookUrl,
                embed,
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