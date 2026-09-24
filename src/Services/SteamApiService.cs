using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DiscordUtilities.Services;

public sealed class SteamApiService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private string _apiKey = "";

    private readonly ConcurrentDictionary<ulong, string> _avatarCache = new();
    private readonly ConcurrentDictionary<ulong, WorkshopMapInfo?> _workshopCache = new();
    private readonly ConcurrentDictionary<string, string?> _mapImageResolvedCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, List<string>> _vauffMapLists = new();
    private long _vauffLastUpdated = 0;
    private bool _vauffListFetched = false;

    public SteamApiService(ILogger logger)
    {
        _logger = logger;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "DiscordUtilities-SwiftlyS2/1.0");

        _ = Task.Run(RefreshVauffMapListAsync);
    }

    public void SetApiKey(string apiKey) => _apiKey = apiKey;

    public void ClearCaches()
    {
        _avatarCache.Clear();
        _mapImageResolvedCache.Clear();
    }

    public async Task RefreshVauffMapListAsync()
    {
        try
        {
            var response = await _httpClient.GetStringAsync("https://vauff.com/mapimgs/list.php");
            using var doc = JsonDocument.Parse(response);

            if (doc.RootElement.TryGetProperty("lastUpdated", out var lastUpdatedElem) &&
                lastUpdatedElem.TryGetInt64(out var lastUpdated))
            {
                if (lastUpdated <= _vauffLastUpdated && _vauffListFetched)
                    return;
                _vauffLastUpdated = lastUpdated;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("lastUpdated") || prop.Value.ValueKind != JsonValueKind.Array)
                    continue;

                var list = new List<string>();
                foreach (var item in prop.Value.EnumerateArray())
                {
                    var map = item.GetString();
                    if (!string.IsNullOrWhiteSpace(map))
                        list.Add(map);
                }

                _vauffMapLists[prop.Name] = list;
            }

            _vauffListFetched = true;
            _logger.LogInformation("[DiscordUtilities] Loaded map image database from vauff.com successfully");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DiscordUtilities] Could not pre-fetch vauff.com map list (will fall back to direct checks)");
        }
    }

    public async Task<string?> GetMapImageAsync(string cleanMapName, ulong? workshopId = null)
    {
        if (string.IsNullOrWhiteSpace(cleanMapName))
            return null;

        var cacheKey = workshopId.HasValue && workshopId.Value > 0 
            ? $"ws_{workshopId.Value}_{cleanMapName}" 
            : cleanMapName;

        if (_mapImageResolvedCache.TryGetValue(cacheKey, out var cachedUrl))
            return cachedUrl;

        // 1. Workshop is always preferred: use the known ID when present, otherwise search the workshop by name
        var wsInfo = workshopId.HasValue && workshopId.Value > 0
            ? await GetWorkshopMapInfoAsync(workshopId.Value)
            : await SearchWorkshopMapByNameAsync(cleanMapName);

        if (!string.IsNullOrWhiteSpace(wsInfo?.PreviewUrl))
        {
            _mapImageResolvedCache[cacheKey] = wsInfo.PreviewUrl;
            return wsInfo.PreviewUrl;
        }

        // 2. Fall back to the vauff.com map image database
        // make sure vauff list is available
        if (!_vauffListFetched)
        {
            await RefreshVauffMapListAsync();
        }

        var mapLower = cleanMapName.ToLowerInvariant();

        // Check all relevant Source game categories on vauff.com
        string[] vauffCategories = ["730_cs2", "730_csgo", "240", "4000", "440", "550", "10"];
        foreach (var category in vauffCategories)
        {
            var match = FindBestVauffMatch(category, mapLower);
            if (!string.IsNullOrWhiteSpace(match))
            {
                var url = $"https://vauff.com/mapimgs/{category}/{Uri.EscapeDataString(match)}.jpg";
                _mapImageResolvedCache[cacheKey] = url;
                return url;
            }
        }

        // Direct HEAD checks across categories for exact map filename
        foreach (var category in vauffCategories)
        {
            var directUrl = $"https://vauff.com/mapimgs/{category}/{Uri.EscapeDataString(mapLower)}.jpg";
            if (await CheckUrlExistsAsync(directUrl))
            {
                _mapImageResolvedCache[cacheKey] = directUrl;
                return directUrl;
            }
        }

        // GameTracker fallback checks across CSS, CS:GO, CS, TF2, GMOD
        string[] gtGames = ["css", "csgo", "cs", "garrysmod", "tf2", "left4dead2"];
        foreach (var game in gtGames)
        {
            var gtUrl = $"https://image.gametracker.com/images/maps/160x120/{game}/{Uri.EscapeDataString(mapLower)}.jpg";
            if (await CheckUrlExistsAsync(gtUrl))
            {
                _mapImageResolvedCache[cacheKey] = gtUrl;
                return gtUrl;
            }
        }

        // Try stripping standard suffixes (_v1, _v2, _fix, _cs2, _b1, _rc1, etc.) for fallback
        var baseMapName = System.Text.RegularExpressions.Regex.Replace(mapLower, @"(_v\d+[\w]*|_fix[\w]*|_cs2|_beta[\w]*|_b\d+[\w]*|_rc\d+[\w]*)$", "");
        if (!string.Equals(baseMapName, mapLower, StringComparison.OrdinalIgnoreCase) && baseMapName.Length > 2)
        {
            foreach (var category in vauffCategories)
            {
                var match = FindBestVauffMatch(category, baseMapName);
                if (!string.IsNullOrWhiteSpace(match))
                {
                    var url = $"https://vauff.com/mapimgs/{category}/{Uri.EscapeDataString(match)}.jpg";
                    _mapImageResolvedCache[cacheKey] = url;
                    return url;
                }
            }

            foreach (var game in gtGames)
            {
                var gtUrl = $"https://image.gametracker.com/images/maps/160x120/{game}/{Uri.EscapeDataString(baseMapName)}.jpg";
                if (await CheckUrlExistsAsync(gtUrl))
                {
                    _mapImageResolvedCache[cacheKey] = gtUrl;
                    return gtUrl;
                }
            }
        }

        _mapImageResolvedCache[cacheKey] = null;
        return null;
    }

    private string? FindBestVauffMatch(string category, string mapLower)
    {
        if (!_vauffMapLists.TryGetValue(category, out var mapList) || mapList.Count == 0)
            return null;

        // Exact match
        var exact = mapList.FirstOrDefault(m => string.Equals(m, mapLower, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;

        // Substring / Prefix match with 31 char limit (standard Source map limit accommodation like Maunz)
        var trimmed = mapLower.Length > 31 ? mapLower[..31] : mapLower;
        string? bestMatch = null;
        var minDistance = int.MaxValue;

        foreach (var candidate in mapList)
        {
            var candLower = candidate.ToLowerInvariant();
            if (trimmed.StartsWith(candLower, StringComparison.OrdinalIgnoreCase) ||
                candLower.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase) ||
                mapLower.Contains(candLower, StringComparison.OrdinalIgnoreCase))
            {
                var distance = ComputeLevenshtein(trimmed, candLower);
                if (distance < minDistance)
                {
                    minDistance = distance;
                    bestMatch = candidate;
                }
            }
        }

        return bestMatch;
    }

    private static int ComputeLevenshtein(string s, string t)
    {
        var n = s.Length;
        var m = t.Length;
        var d = new int[n + 1, m + 1];

        if (n == 0) return m;
        if (m == 0) return n;

        for (var i = 0; i <= n; d[i, 0] = i++) { }
        for (var j = 0; j <= m; d[0, j] = j++) { }

        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= m; j++)
            {
                var cost = (t[j - 1] == s[i - 1]) ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[n, m];
    }

    private static string NormalizeMapName(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var c in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    private async Task<bool> CheckUrlExistsAsync(string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            var res = await _httpClient.SendAsync(req);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> GetPlayerAvatarAsync(ulong steamId64)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            return null;

        if (_avatarCache.TryGetValue(steamId64, out var cached))
            return cached;

        try
        {
            var url = $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={_apiKey}&steamids={steamId64}";
            var response = await _httpClient.GetStringAsync(url);
            using var doc = JsonDocument.Parse(response);

            var players = doc.RootElement.GetProperty("response").GetProperty("players");

            foreach (var player in players.EnumerateArray())
            {
                if (player.TryGetProperty("avatarfull", out var avatar))
                {
                    var avatarUrl = avatar.GetString() ?? "";
                    _avatarCache[steamId64] = avatarUrl;
                    return avatarUrl;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DiscordUtilities] Failed to fetch avatar for {SteamId}", steamId64);
        }

        return null;
    }

    public async Task<WorkshopMapInfo?> SearchWorkshopMapByNameAsync(string mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName))
            return null;

        var cleanName = mapName;
        var lastSlash = mapName.LastIndexOfAny(['/', '\\']);
        if (lastSlash >= 0 && lastSlash < mapName.Length - 1)
            cleanName = mapName[(lastSlash + 1)..];

        try
        {
            var keyParam = !string.IsNullOrWhiteSpace(_apiKey) ? $"&key={_apiKey}" : "";
            var url = $"https://api.steampowered.com/IPublishedFileService/QueryFiles/v1/?appid=730&search_text={Uri.EscapeDataString(cleanName)}&return_previews=true&return_short_description=true{keyParam}";

            var response = await _httpClient.GetStringAsync(url);
            using var doc = JsonDocument.Parse(response);

            if (!doc.RootElement.TryGetProperty("response", out var resp) ||
                !resp.TryGetProperty("publishedfiledetails", out var details) ||
                details.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var cleanNorm = NormalizeMapName(cleanName);
            if (cleanNorm.Length == 0) return null;

            WorkshopMapInfo? bestMatch = null;
            var bestScore = int.MaxValue;

            foreach (var item in details.EnumerateArray())
            {
                if (!item.TryGetProperty("publishedfileid", out var idProp))
                    continue;

                var idStr = idProp.GetString() ?? idProp.GetRawText();
                if (!ulong.TryParse(idStr, out var wsId) || wsId == 0)
                    continue;

                var title = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
                var previewUrl = item.TryGetProperty("preview_url", out var prevProp) ? prevProp.GetString() ?? "" : "";
                var desc = item.TryGetProperty("short_description", out var descProp) ? descProp.GetString() ?? "" : "";

                if (string.IsNullOrWhiteSpace(previewUrl)) continue;

                var info = new WorkshopMapInfo
                {
                    WorkshopId = wsId,
                    Title = string.IsNullOrWhiteSpace(title) ? cleanName : title,
                    PreviewUrl = previewUrl,
                    Description = desc
                };

                _workshopCache[wsId] = info;

                // Only trust results whose title actually refers to the requested map
                var titleNorm = NormalizeMapName(title);
                if (titleNorm.Length == 0) continue;
                if (titleNorm.Contains(cleanNorm, StringComparison.Ordinal) ||
                    cleanNorm.Contains(titleNorm, StringComparison.Ordinal))
                {
                    var score = Math.Abs(titleNorm.Length - cleanNorm.Length);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestMatch = info;
                    }
                }
            }

            if (bestMatch != null) return bestMatch;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DiscordUtilities] Steam Workshop Search failed for {MapName}", cleanName);
        }

        return null;
    }

    public async Task<WorkshopMapInfo?> GetWorkshopMapInfoAsync(ulong workshopId)
    {
        if (_workshopCache.TryGetValue(workshopId, out var cached))
            return cached;

        try
        {
            var content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("itemcount", "1"),
                new KeyValuePair<string, string>("publishedfileids[0]", workshopId.ToString())
            ]);

            var response = await _httpClient.PostAsync(
                "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/", content);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var details = doc.RootElement.GetProperty("response").GetProperty("publishedfiledetails");

            foreach (var item in details.EnumerateArray())
            {
                var info = new WorkshopMapInfo
                {
                    WorkshopId = workshopId,
                    Title = item.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                    PreviewUrl = item.TryGetProperty("preview_url", out var preview) ? preview.GetString() ?? "" : "",
                    Description = item.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : ""
                };

                _workshopCache[workshopId] = info;
                return info;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DiscordUtilities] Failed to fetch workshop info for {WorkshopId}", workshopId);
        }

        _workshopCache[workshopId] = null;
        return null;
    }

    public static (bool IsWorkshop, ulong WorkshopId) TryParseWorkshopId(string mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName))
            return (false, 0);

        var parts = mapName.Replace('\\', '/').Split('/');

        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals("workshop", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < parts.Length && ulong.TryParse(parts[i + 1], out var workshopId))
            {
                return (true, workshopId);
            }
        }

        return (false, 0);
    }

    public void Dispose() => _httpClient.Dispose();
}

public sealed class WorkshopMapInfo
{
    public ulong WorkshopId { get; set; }
    public string Title { get; set; } = "";
    public string PreviewUrl { get; set; } = "";
    public string Description { get; set; } = "";
    public string WorkshopUrl => $"https://steamcommunity.com/sharedfiles/filedetails/?id={WorkshopId}";
}

