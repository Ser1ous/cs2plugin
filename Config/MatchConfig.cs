using System.Text.Json.Serialization;

namespace CS2MatchPlugin.Config;

public class MatchConfig
{
    [JsonPropertyName("matchid")]
    public string MatchId { get; set; } = "";

    [JsonPropertyName("lobbyid")]
    public string LobbyId { get; set; } = "";

    [JsonPropertyName("num_maps")]
    public int NumMaps { get; set; } = 1;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "competitive";

    [JsonPropertyName("maplist")]
    public List<string> Maplist { get; set; } = new();

    [JsonPropertyName("map_sides")]
    public List<string> MapSides { get; set; } = new();

    [JsonPropertyName("clinch_series")]
    public bool ClinchSeries { get; set; } = true;

    [JsonPropertyName("players_per_team")]
    public int PlayersPerTeam { get; set; } = 5;

    [JsonPropertyName("team1")]
    public TeamConfig Team1 { get; set; } = new();

    [JsonPropertyName("team2")]
    public TeamConfig Team2 { get; set; } = new();

    [JsonPropertyName("cvars")]
    public Dictionary<string, string> Cvars { get; set; } = new();
}

public class TeamConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("players")]
    public Dictionary<string, string> Players { get; set; } = new();

    public static bool IsBotId(string playerId) =>
        playerId.StartsWith("BOT_", StringComparison.OrdinalIgnoreCase);

    public int BotCount
    {
        get
        {
            int count = 0;
            foreach (var key in Players.Keys)
                if (IsBotId(key)) count++;
            return count;
        }
    }
}
