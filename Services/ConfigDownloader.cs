using System.Text.Json;
using CS2MatchPlugin.Config;

namespace CS2MatchPlugin.Services;

public class ConfigDownloader
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<MatchConfig> DownloadAsync(string url)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(url);
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to fetch config from '{url}': {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
            throw new Exception($"HTTP {(int)response.StatusCode} when fetching config from '{url}'");

        string body = await response.Content.ReadAsStringAsync();

        MatchConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<MatchConfig>(body, _jsonOptions);
        }
        catch (JsonException ex)
        {
            throw new Exception($"Failed to parse match config JSON: {ex.Message}", ex);
        }

        if (config == null)
            throw new Exception("Match config JSON deserialized to null");

        if (config.Maplist.Count == 0)
            throw new Exception("Match config has no maps in maplist");

        return config;
    }
}
