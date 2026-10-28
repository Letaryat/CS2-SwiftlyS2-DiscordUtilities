using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace DiscordUtilities.Services;

public sealed class WebhookService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, DateTime> _cooldowns = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public WebhookService(ILogger logger)
    {
        _logger = logger;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "DiscordUtilities-SwiftlyS2/1.0");
    }

    public async Task<bool> ValidateWebhookAsync(string webhookUrl)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl)) return false;

        try
        {
            var response = await _httpClient.GetAsync(webhookUrl);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DiscordUtilities] Failed to validate webhook URL: {Url}", webhookUrl);
            return false;
        }
    }

    public bool IsOnCooldown(string key, int cooldownSeconds)
    {
        if (cooldownSeconds <= 0) return false;

        var now = DateTime.UtcNow;
        if (_cooldowns.TryGetValue(key, out var lastSent))
        {
            if ((now - lastSent).TotalSeconds < cooldownSeconds)
                return true;
        }

        _cooldowns[key] = now;
        return false;
    }

    public async Task SendEmbedAsync(string webhookUrl, DiscordEmbed embed, string? username = null, string? avatarUrl = null, string? content = null, List<DiscordComponentActionRow>? components = null)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl)) return;

        var payload = new WebhookPayload
        {
            Content = content,
            Username = username,
            AvatarUrl = avatarUrl,
            Embeds = [embed],
            Components = components
        };

        await SendPayloadAsync(webhookUrl, payload);
    }

    public async Task SendMessageAsync(string webhookUrl, string content, string? username = null, string? avatarUrl = null)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl)) return;

        var payload = new WebhookPayload
        {
            Content = content,
            Username = username,
            AvatarUrl = avatarUrl
        };

        await SendPayloadAsync(webhookUrl, payload);
    }

    private async Task SendPayloadAsync(
        string webhookUrl,
        object payload)
    {
        try
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);

            using var httpContent = new StringContent(
                json,
                Encoding.UTF8,
                "application/json"
            );

            var response = await _httpClient.PostAsync(
                webhookUrl,
                httpContent
            );

            if ((int)response.StatusCode == 429)
            {
                var body = await response.Content.ReadAsStringAsync();

                _logger.LogWarning(
                    "[DiscordUtilities] Rate limited by Discord: {Body}",
                    body
                );

                using var doc = JsonDocument.Parse(body);

                if (doc.RootElement.TryGetProperty(
                        "retry_after",
                        out var retryAfter))
                {
                    var waitMs =
                        (int)(retryAfter.GetDouble() * 1000) + 100;

                    await Task.Delay(waitMs);

                    using var retryContent = new StringContent(
                        json,
                        Encoding.UTF8,
                        "application/json"
                    );

                    await _httpClient.PostAsync(
                        webhookUrl,
                        retryContent
                    );
                }
            }
            else if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();

                _logger.LogError(
                    "[DiscordUtilities] Webhook failed ({StatusCode}): {Body}",
                    (int)response.StatusCode,
                    body
                );
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "[DiscordUtilities] Failed to send webhook"
            );
        }
    }

    public static int ParseColor(string hex)
    {
        hex = hex.TrimStart('#');
        if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var color))
            return color;
        return 0x5865F2;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    public async Task SendComponentsV2Async(
        string webhookUrl,
        List<DiscordComponentV2> components,
        string? username = null,
        string? avatarUrl = null)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
            return;

        var separator = webhookUrl.Contains('?') ? '&' : '?';
        var url = $"{webhookUrl}{separator}with_components=true";

        var payload = new WebhookComponentsV2Payload
        {
            Flags = 32768, // IS_COMPONENTS_V2
            Username = username,
            AvatarUrl = avatarUrl,
            Components = components
        };

        await SendPayloadAsync(url, payload);
    }
}

#region Webhook Models

public sealed class WebhookPayload
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; set; }

    [JsonPropertyName("embeds")]
    public List<DiscordEmbed>? Embeds { get; set; }

    [JsonPropertyName("components")]
    public List<DiscordComponentActionRow>? Components { get; set; }
}

public sealed class DiscordComponentActionRow
{
    [JsonPropertyName("type")]
    public int Type { get; set; } = 1; // 1 = ActionRow

    [JsonPropertyName("components")]
    public List<DiscordComponentButton> Components { get; set; } = [];
}

public sealed class DiscordComponentButton
{
    [JsonPropertyName("type")]
    public int Type { get; set; } = 2; // 2 = Button

    [JsonPropertyName("style")]
    public int Style { get; set; } = 5; // 5 = Link Button

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("emoji")]
    public DiscordEmoji? Emoji { get; set; }
}

public sealed class DiscordEmoji
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

public sealed class DiscordEmbed
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("color")]
    public int? Color { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("footer")]
    public EmbedFooter? Footer { get; set; }

    [JsonPropertyName("thumbnail")]
    public EmbedImage? Thumbnail { get; set; }

    [JsonPropertyName("image")]
    public EmbedImage? Image { get; set; }

    [JsonPropertyName("author")]
    public EmbedAuthor? Author { get; set; }

    [JsonPropertyName("fields")]
    public List<EmbedField>? Fields { get; set; }

    public DiscordEmbed AddField(string name, string value, bool inline = false)
    {
        Fields ??= [];
        Fields.Add(new EmbedField { Name = name, Value = value, Inline = inline });
        return this;
    }

    public DiscordEmbed WithTimestamp()
    {
        Timestamp = DateTime.UtcNow.ToString("o");
        return this;
    }
}

public sealed class EmbedFooter
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; set; }
}

public sealed class EmbedImage
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public sealed class EmbedAuthor
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; set; }
}

public sealed class EmbedField
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("inline")]
    public bool? Inline { get; set; }
}

public sealed class WebhookComponentsV2Payload
{
    [JsonPropertyName("flags")]
    public int Flags { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; set; }

    [JsonPropertyName("components")]
    public List<DiscordComponentV2> Components { get; set; } = [];
}

public sealed class DiscordComponentV2
{
    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("id")]
    public int? Id { get; set; }

    [JsonPropertyName("accent_color")]
    public int? AccentColor { get; set; }

    [JsonPropertyName("components")]
    public List<DiscordComponentV2>? Components { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("accessory")]
    public DiscordComponentV2? Accessory { get; set; }

    [JsonPropertyName("media")]
    public DiscordMedia? Media { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("divider")]
    public bool? Divider { get; set; }

    [JsonPropertyName("spacing")]
    public int? Spacing { get; set; }

    [JsonPropertyName("style")]
    public int? Style { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public sealed class DiscordMedia
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";
}

#endregion
