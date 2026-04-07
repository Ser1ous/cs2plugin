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

    // --- Core ---
    public int Kills         { get; set; }
    public int Deaths        { get; set; }
    public int Assists       { get; set; }
    public int Headshots     { get; set; }

    // --- Damage ---
    public int DamageDealt   { get; set; }  // total HP damage dealt (all weapons)
    public int DamageTaken   { get; set; }  // total HP damage received
    public int HeDamageDealt { get; set; }  // HE grenade damage dealt
    public int HeDamageTaken { get; set; }  // HE grenade damage received
    public int UtilDamage    { get; set; }  // molotov/incendiary damage dealt
    public int ArmorDamage   { get; set; }  // armor damage dealt

    // --- Flash ---
    public int EnemiesFlashed       { get; set; }  // number of enemies blinded by this player
    public float TotalFlashDuration { get; set; }  // total seconds of blindness caused
    public int FlashAssists         { get; set; }  // kills where player flashed the victim

    // --- Utility ---
    public int GrenadesThrown { get; set; }  // all grenades thrown

    // --- Bomb ---
    public int BombPlants  { get; set; }
    public int BombDefuses { get; set; }

    // --- Tracking ---
    public int RoundsPlayed { get; set; }
    public int LastRound    { get; set; }
}
