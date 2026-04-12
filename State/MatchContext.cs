using CS2MatchPlugin.Config;

namespace CS2MatchPlugin.State;

public class MatchContext
{
    public MatchConfig Config { get; set; } = null!;
    public MatchState State { get; set; } = MatchState.None;
    public MatchState StateBeforePause { get; set; } = MatchState.None;

    public bool PendingWarmup { get; set; } = false;
    public string DemoName { get; set; } = "";
    public string DemoTimestamp { get; set; } = ""; // captured at map load, matches tv_autorecord filename
    public int CurrentMapIndex { get; set; } = 0;

    public HashSet<ulong> ReadyPlayers { get; set; } = new();

    public TeamSide KnifeWinnerCsTeam { get; set; } = TeamSide.None;
    public int KnifeWinnerConfigTeam { get; set; } = 0;

    public HashSet<ulong> UnpauseVotes { get; set; } = new();
    public bool Team1UnpauseVoted { get; set; } = false;
    public bool Team2UnpauseVoted { get; set; } = false;

    public int Team1Score { get; set; } = 0;
    public int Team2Score { get; set; } = 0;

    // Tracks the current round number. Set at the start of each new round
    // (in ResetRoundContext) so it stays pinned to the correct round even
    // after OnRoundEnd increments Team1Score/Team2Score. Post-round events
    // (e.g. planted_c4 suicide) therefore still read the right round number.
    public int CurrentRound { get; set; } = 0;
    public int MapWinsTeam1 { get; set; } = 0;
    public int MapWinsTeam2 { get; set; } = 0;
    // Maps that ended in a draw (single-OT rule). Counted toward series
    // progression but credited to neither side.
    public int MapDraws    { get; set; } = 0;

    public TeamSide Team1Side { get; set; } = TeamSide.None;
    public TeamSide Team2Side { get; set; } = TeamSide.None;

    // Per-player live stats — key is SteamID64
    public Dictionary<ulong, PlayerStats> PlayerStats { get; set; } = new();

    // Bomb defuse tracking: steamId → defuse attempt count this round (reset each round)
    public Dictionary<ulong, int> DefuseAttempts { get; set; } = new();

    // Which player is currently defusing (steamId), or 0 if none
    public ulong ActiveDefuser { get; set; } = 0;

    // Most recent defuse attempt this round, kept around even if the player
    // aborts so OnBombExploded can attribute "X needed N more seconds".
    // SteamId == 0 means no defuse was attempted this round.
    public ulong LastDefuserSteamId { get; set; } = 0;
    public string LastDefuserName   { get; set; } = "";
    public bool   LastDefuserHasKit { get; set; } = false;
    public float  LastDefuseStartTime { get; set; } = 0f;

    // Server time when bomb was planted this round (0 = not planted)
    public float BombPlantTime { get; set; } = 0f;
    // mp_c4timer value (seconds); default 40
    public float BombTimerLength { get; set; } = 40f;

    // --- Per-round tracking (reset each round) ---

    // Entry frag: steamId of first killer this round (0 = no kill yet)
    public ulong EntryKillerThisRound { get; set; } = 0;
    public int EntryKillerConfigTeam  { get; set; } = 0;

    // Alive players per config team — populated by spawn events, cleared on death
    public HashSet<ulong> AliveTeam1 { get; set; } = new();
    public HashSet<ulong> AliveTeam2 { get; set; } = new();

    // Clutch: the last surviving player on their team vs N enemies
    // 0 = no clutch in progress
    public ulong ClutchPlayerId   { get; set; } = 0;
    public int   ClutchSituation  { get; set; } = 0;  // 1 = 1v1, 2 = 1v2
    public int   ClutchPlayerConfigTeam { get; set; } = 0;

    // Util success: tracks which players already scored a util hit this round
    // (prevents counting every tick of a molotov as a separate success)
    public HashSet<ulong> RoundUtilSucceeded { get; set; } = new();

    // Flash success: tracks which players already scored a flash hit this round
    // (prevents counting each blinded enemy as a separate flash success)
    public HashSet<ulong> RoundFlashSucceeded { get; set; } = new();

    // Per-player HP tracking — set to 100 on spawn, decremented on every player_hurt.
    // Used to compute actual health lost (caps overkill damage correctly for stats).
    // Updated for ALL damage (FF and enemy) so enemy stats stay accurate after FF hits.
    public Dictionary<ulong, int> PlayerCurrentHp { get; set; } = new();

    // Set to true when the bomb explodes or the round timer ends this round.
    // Kills that occur after either event are tagged after_time_is_out = 1.
    public bool BombExploded { get; set; } = false;
    public bool RoundEnded   { get; set; } = false;
}

public class PlayerStats
{
    public string PlayerName { get; set; } = "";
    public int ConfigTeam    { get; set; }
    public string TeamName   { get; set; } = "";

    // --- Core (cumulative) ---
    public int Kills    { get; set; }
    public int Deaths   { get; set; }
    public int Assists  { get; set; }
    public int Headshots { get; set; }

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
    public int UtilDamage    { get; set; }   // molotov/incendiary
    public int ArmorDamage   { get; set; }

    // --- Shots ---
    public int ShotsFired    { get; set; }   // weapon_fire events
    public int ShotsOnTarget { get; set; }   // player_hurt events (each hit = 1 shot on target)

    // --- Flash ---
    public int EnemiesFlashed       { get; set; }
    public float TotalFlashDuration { get; set; }
    public int FlashAssists         { get; set; }
    public int FlashCount           { get; set; }   // flashbangs thrown
    public int FlashSuccesses       { get; set; }   // flashes that blinded ≥1 enemy

    // --- Utility ---
    public int GrenadesThrown   { get; set; }
    public int UtilSuccesses    { get; set; }  // util grenades that damaged ≥1 enemy
    public int UtilEnemiesHit   { get; set; }  // total enemies hit by util grenades

    // --- Economy ---
    public int EquipmentValue   { get; set; }  // value at round start (from player_spawn/freeze end)
    public int MoneySpent       { get; set; }  // money spent buying this round
    public int MoneyRemaining   { get; set; }  // money left after buying (= money_saved)
    public int KillReward       { get; set; }  // cash earned from kills this round (cumulative)
    public int CashEarned       { get; set; }  // total cash earned this match

    // --- Time alive ---
    public float LiveTimeSeconds { get; set; }  // total seconds alive across all rounds
    public float RoundSpawnTime  { get; set; }  // Server time when spawned this round (0 = not alive)

    // --- Clutch tracking ---
    public int V1Count { get; set; }  // times entered 1v1
    public int V1Wins  { get; set; }
    public int V2Count { get; set; }  // times entered 1v2
    public int V2Wins  { get; set; }

    // --- Entry frag ---
    public int EntryCount { get; set; }  // rounds where player got the first kill
    public int EntryWins  { get; set; }  // of those, rounds the team won

    // --- MVP ---
    public int Mvps { get; set; }

    // --- Bomb ---
    public int BombPlants  { get; set; }
    public int BombDefuses { get; set; }
    public int DefuseAttempts { get; set; }  // times started defusing (includes failed)

    // --- Per-round counters (reset at each round end after flush) ---
    public int RoundKills          { get; set; }
    public int RoundDeaths         { get; set; }
    public int RoundDamageDealt    { get; set; }
    public int RoundHeadshots      { get; set; }
    public int RoundAssists        { get; set; }
    public int RoundEquipmentValue { get; set; }
    public int RoundStartAccount   { get; set; }  // money at round start (before buying)
    public int RoundMoneySpent     { get; set; }  // money spent buying this round
    public int RoundCashEarned     { get; set; }  // cash earned after freeze end (kills + bonus)
    public bool RoundGotEntry      { get; set; }  // got first kill this round

    // --- Tracking ---
    public int RoundsPlayed { get; set; }
    public int LastRound    { get; set; }
}
