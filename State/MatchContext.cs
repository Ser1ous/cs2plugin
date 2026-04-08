using CS2MatchPlugin.Config;

namespace CS2MatchPlugin.State;

public class MatchContext
{
    public MatchConfig Config { get; set; } = null!;
    public MatchState State { get; set; } = MatchState.None;
    public MatchState StateBeforePause { get; set; } = MatchState.None;

    /// <summary>
    /// True when we are waiting for the correct map to finish loading.
    /// EnterWarmup will be called on the first RoundStart after the map is active.
    /// </summary>
    public bool PendingWarmup { get; set; } = false;

    public int CurrentMapIndex { get; set; } = 0;

    // SteamID64s that typed !ready during warmup
    public HashSet<ulong> ReadyPlayers { get; set; } = new();

    // Which CS team (by TeamNum int) won the knife round
    public TeamSide KnifeWinnerCsTeam { get; set; } = TeamSide.None;

    // Which config team number won knife (1 or 2), 0 = unknown
    public int KnifeWinnerConfigTeam { get; set; } = 0;

    // Unpause votes tracked by steamid
    public HashSet<ulong> UnpauseVotes { get; set; } = new();
    public bool Team1UnpauseVoted { get; set; } = false;
    public bool Team2UnpauseVoted { get; set; } = false;

    // Running scores for current map
    public int Team1Score { get; set; } = 0;
    public int Team2Score { get; set; } = 0;

    // Series wins
    public int MapWinsTeam1 { get; set; } = 0;
    public int MapWinsTeam2 { get; set; } = 0;

    // Side assignments after knife: TeamSide.CT or TeamSide.T
    public TeamSide Team1Side { get; set; } = TeamSide.None;
    public TeamSide Team2Side { get; set; } = TeamSide.None;

    // Per-player live stats — key is SteamID64
    public Dictionary<ulong, PlayerStats> PlayerStats { get; set; } = new();
}

public class PlayerStats
{
    public string PlayerName { get; set; } = "";
    public int ConfigTeam    { get; set; }  // 1 or 2
    public string TeamName   { get; set; } = "";

    // --- Core (cumulative) ---
    public int Kills         { get; set; }
    public int Deaths        { get; set; }
    public int Assists       { get; set; }
    public int Headshots     { get; set; }

    // --- Multi-kills (cumulative) ---
    public int Kills5k { get; set; }
    public int Kills4k { get; set; }
    public int Kills3k { get; set; }
    public int Kills2k { get; set; }

    // --- Damage ---
    public int DamageDealt   { get; set; }
    public int DamageTaken   { get; set; }
    public int HeDamageDealt { get; set; }
    public int HeDamageTaken { get; set; }
    public int UtilDamage    { get; set; }
    public int ArmorDamage   { get; set; }

    // --- Flash ---
    public int EnemiesFlashed       { get; set; }
    public float TotalFlashDuration { get; set; }
    public int FlashAssists         { get; set; }

    // --- Utility ---
    public int GrenadesThrown { get; set; }

    // --- Bomb ---
    public int BombPlants  { get; set; }
    public int BombDefuses { get; set; }

    // --- Per-round counters (reset at each round end) ---
    public int RoundKills       { get; set; }
    public int RoundDeaths      { get; set; }
    public int RoundDamageDealt { get; set; }
    public int RoundHeadshots   { get; set; }
    public int RoundAssists     { get; set; }

    // --- Tracking ---
    public int RoundsPlayed { get; set; }
    public int LastRound    { get; set; }
}
