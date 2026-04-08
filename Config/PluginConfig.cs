using CounterStrikeSharp.API.Core;

namespace CS2MatchPlugin.Config;

public class PluginConfig : BasePluginConfig
{
    public string MySqlHost { get; set; } = "127.0.0.1";
    public int MySqlPort { get; set; } = 3306;
    public string MySqlDatabase { get; set; } = "cs2_matches";
    public string MySqlUser { get; set; } = "cs2user";
    public string MySqlPassword { get; set; } = "";

    public string AimMapName { get; set; } = "aim_map";
    public string AimCfgName { get; set; } = "aim";
    public string WarmupCfgName { get; set; } = "warmup";
    public string KnifeCfgName { get; set; } = "knife";
    public string CompetitiveCfgName { get; set; } = "competitive";

    // Minimum players per team required to start (0 = use match config value)
    public int MinPlayersToStart { get; set; } = 0;


}
