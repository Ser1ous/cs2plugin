using CS2MatchPlugin.Config;

namespace CS2MatchPlugin.State;

public class MatchContext
{
    public MatchConfig Config { get; set; } = null!;
    public MatchState State { get; set; } = MatchState.None;
    public MatchState StateBeforePause { get; set; } = MatchState.None;

    public bool PendingWarmup { get; set; } = false;
    public string DemoName { get; set; } = "";
    public int CurrentMapIndex { get; set; } = 0;

    public HashSet<ulong> ReadyPlayers { get; set; } = new();

    public int BotCountTeam1 { get; set; } = 0;
    public int BotCountTeam2 { get; set; } = 0;

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
    public int   ClutchSituation  { get; set; } = 0;  // 1 = 1v1, 2 = 1v2, 3 = 1v3, 4 = 1v4, 5 = 1v5
    public int   ClutchPlayerConfigTeam { get; set; } = 0;

    // Util success: tracks which players already scored a util hit this round
    // (prevents counting every tick of a molotov as a separate success)
    public HashSet<ulong> RoundUtilSucceeded { get; set; } = new();

    // Flash success: tracks which players already scored a flash hit this round
    // (prevents counting each blinded enemy as a separate flash success)
    public HashSet<ulong> RoundFlashSucceeded { get; set; } = new();

    // Per-player snapshots of engine MatchStats at round start (freeze end).
    // Used to compute per-round deltas at round end.
    public Dictionary<ulong, EngineStatsSnapshot> EngineSnapshots { get; set; } = new();

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
    public bool IsBot        { get; set; }

    // =====================================================================
    // Engine-sourced stats (overwritten by SyncStatsFromEngine at round end
    // via player.ActionTrackingServices.MatchStats — MatchZy approach).
    // Manual accumulation is kept as a fallback in case the pawn is invalid
    // at flush time, but the engine values are authoritative.
    // =====================================================================

    // --- Core ---
    public int Kills    { get; set; }
    public int Deaths   { get; set; }
    public int Assists  { get; set; }
    public int Headshots { get; set; }

    // --- Multi-kills ---
    public int Kills5k { get; set; }
    public int Kills4k { get; set; }
    public int Kills3k { get; set; }
    public int Kills2k { get; set; }

    // --- Damage ---
    public int DamageDealt { get; set; }
    public int HealthPointsRemovedTotal { get; set; }
    public int HealthPointsDealtTotal   { get; set; }

    // --- Utility ---
    public int GrenadesThrown   { get; set; }  // utility_count
    public int UtilDamage       { get; set; }
    public int UtilSuccesses    { get; set; }
    public int UtilEnemiesHit   { get; set; }

    // --- Flash ---
    public int FlashCount     { get; set; }
    public int FlashSuccesses { get; set; }
    public int EnemiesFlashed { get; set; }

    // --- Shots ---
    public int ShotsFired    { get; set; }
    public int ShotsOnTarget { get; set; }

    // --- Clutch (1v1, 1v2 from engine) ---
    public int V1Count { get; set; }
    public int V1Wins  { get; set; }
    public int V2Count { get; set; }
    public int V2Wins  { get; set; }

    // --- Entry ---
    public int EntryCount { get; set; }
    public int EntryWins  { get; set; }

    // --- Economy ---
    public int EquipmentValue { get; set; }
    public int MoneySaved     { get; set; }
    public int KillReward     { get; set; }
    public int CashEarned     { get; set; }

    // --- Time alive ---
    public int LiveTime { get; set; }  // seconds alive (int, from engine)

    // --- MVP ---
    public int Mvps  { get; set; }
    public int Score { get; set; }

    // =====================================================================
    // Manually-tracked stats (NOT available from engine MatchStats)
    // =====================================================================

    // --- Bomb ---
    public int BombPlants     { get; set; }
    public int BombDefuses    { get; set; }
    public int DefuseAttempts { get; set; }

    // --- Flash assist ---
    public int FlashAssists { get; set; }

    // --- Clutch 1v3 / 1v4 / 1v5 (engine only has 1v1 and 1v2) ---
    public int V3Count { get; set; }
    public int V3Wins  { get; set; }
    public int V4Count { get; set; }
    public int V4Wins  { get; set; }
    public int V5Count { get; set; }
    public int V5Wins  { get; set; }

    // --- Spawn time (for round-end live-time delta if engine unavailable) ---
    public float RoundSpawnTime { get; set; }

    // =====================================================================
    // Per-round counters (reset after each round flush)
    // =====================================================================
    public int RoundKills          { get; set; }
    public int RoundDeaths         { get; set; }
    public int RoundDamageDealt    { get; set; }  // computed from engine delta
    public int RoundHeadshots      { get; set; }
    public int RoundAssists        { get; set; }
    public int RoundEquipmentValue { get; set; }
    public int RoundStartAccount   { get; set; }
    public int RoundMoneySpent     { get; set; }
    public int RoundCashEarned     { get; set; }
    public bool RoundGotEntry      { get; set; }

    // --- Tracking ---
    public int RoundsPlayed { get; set; }
    public int LastRound    { get; set; }
}

/// <summary>
/// Snapshot of engine MatchStats values at round start (freeze end).
/// Used to compute per-round deltas at round end.
/// </summary>
public struct EngineStatsSnapshot
{
    public int Damage;
    public int Kills;
    public int Deaths;
    public int Assists;
    public int HeadShotKills;
}
