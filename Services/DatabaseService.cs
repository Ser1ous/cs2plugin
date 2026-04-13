using MySqlConnector;
using CS2MatchPlugin.Config;

namespace CS2MatchPlugin.Services;

public class DatabaseService
{
    private MySqlDataSource? _dataSource;
    private bool _initialized = false;

    public void Configure(PluginConfig config)
    {
        string connStr =
            $"Server={config.MySqlHost};Port={config.MySqlPort};Database={config.MySqlDatabase};" +
            $"User ID={config.MySqlUser};Password={config.MySqlPassword};" +
            $"AllowPublicKeyRetrieval=true;SslMode=None;CharSet=utf8mb4;";
        _dataSource = new MySqlDataSourceBuilder(connStr).Build();
    }

    public async Task InitializeTablesAsync()
    {
        if (_dataSource == null)
        {
            Console.WriteLine("[CS2Match] Database not configured, skipping table init");
            return;
        }

        // Split on double-newline so comments with semicolons don't trip the splitter
        var statements = new[]
        {
            @"CREATE TABLE IF NOT EXISTS match_events (
    id          BIGINT AUTO_INCREMENT PRIMARY KEY,
    match_id    VARCHAR(64) NOT NULL,
    event_type  VARCHAR(64) NOT NULL,
    data        JSON,
    created_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX idx_match_id (match_id)
)",

            @"CREATE TABLE IF NOT EXISTS matches (
    id               BIGINT AUTO_INCREMENT PRIMARY KEY,
    match_id         VARCHAR(64) NOT NULL UNIQUE,
    lobby_id         VARCHAR(64) NOT NULL DEFAULT '',
    team1_name       VARCHAR(128) NOT NULL DEFAULT '',
    team2_name       VARCHAR(128) NOT NULL DEFAULT '',
    team1_score      INT NOT NULL DEFAULT 0,
    team2_score      INT NOT NULL DEFAULT 0,
    map_name         VARCHAR(128) NOT NULL DEFAULT '',
    num_maps         INT NOT NULL DEFAULT 1,
    current_map      INT NOT NULL DEFAULT 1,
    status           ENUM('warmup','knife','sidepick','live','paused','finished') NOT NULL DEFAULT 'warmup',
    started_at       DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    finished_at      DATETIME,
    INDEX idx_match_id (match_id),
    INDEX idx_lobby_id (lobby_id)
)",

            @"CREATE TABLE IF NOT EXISTS kill_events (
    id                      BIGINT AUTO_INCREMENT PRIMARY KEY,
    match_id                VARCHAR(64) NOT NULL,
    round                   INT NOT NULL,
    attacker_steamid        BIGINT UNSIGNED,
    attacker_name           VARCHAR(128),
    attacker_team           TINYINT,
    victim_steamid          BIGINT UNSIGNED NOT NULL,
    victim_name             VARCHAR(128),
    victim_team             TINYINT,
    assister_steamid        BIGINT UNSIGNED,
    assister_name           VARCHAR(128),
    assisted_flash          TINYINT(1) NOT NULL DEFAULT 0,
    weapon                  VARCHAR(64),
    headshot                TINYINT(1) NOT NULL DEFAULT 0,
    thru_smoke              TINYINT(1) NOT NULL DEFAULT 0,
    attacker_blind          TINYINT(1) NOT NULL DEFAULT 0,
    no_scope                TINYINT(1) NOT NULL DEFAULT 0,
    penetrated              TINYINT(1) NOT NULL DEFAULT 0,
    attacker_in_air         TINYINT(1) NOT NULL DEFAULT 0,
    dmg_health              INT NOT NULL DEFAULT 0,
    dmg_armor               INT NOT NULL DEFAULT 0,
    attacker_x              FLOAT,
    attacker_y              FLOAT,
    attacker_z              FLOAT,
    victim_x                FLOAT,
    victim_y                FLOAT,
    victim_z                FLOAT,
    after_time_is_out       TINYINT(1) NOT NULL DEFAULT 0,
    part_of_body            INT,
    created_at              DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX idx_match_round (match_id, round)
)",

            @"CREATE TABLE IF NOT EXISTS chat_events (
    id          BIGINT AUTO_INCREMENT PRIMARY KEY,
    match_id    VARCHAR(64) NOT NULL,
    round       INT NOT NULL,
    steamid     BIGINT UNSIGNED NOT NULL,
    player_name VARCHAR(128),
    message     TEXT NOT NULL,
    team_only   TINYINT(1) NOT NULL DEFAULT 0,
    created_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX idx_match_round (match_id, round)
)",

            @"CREATE TABLE IF NOT EXISTS round_events (
    id          BIGINT AUTO_INCREMENT PRIMARY KEY,
    match_id    VARCHAR(64) NOT NULL,
    round       INT NOT NULL,
    event_type  VARCHAR(64) NOT NULL,
    team1_score INT NOT NULL DEFAULT 0,
    team2_score INT NOT NULL DEFAULT 0,
    winner_team TINYINT,
    data        JSON,
    created_at  DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX idx_match_id (match_id)
)",

            @"CREATE TABLE IF NOT EXISTS match_scoreboard (
    id                   BIGINT AUTO_INCREMENT PRIMARY KEY,
    match_id             VARCHAR(64) NOT NULL,
    steamid              BIGINT UNSIGNED NOT NULL,
    player_name          VARCHAR(255),
    config_team          TINYINT NOT NULL,
    team_name            VARCHAR(255) NOT NULL DEFAULT '',
    kills                INT NOT NULL DEFAULT 0,
    deaths               INT NOT NULL DEFAULT 0,
    damage_dealt         INT NOT NULL DEFAULT 0,
    assists              INT NOT NULL DEFAULT 0,
    kills_5k             INT NOT NULL DEFAULT 0,
    kills_4k             INT NOT NULL DEFAULT 0,
    kills_3k             INT NOT NULL DEFAULT 0,
    kills_2k             INT NOT NULL DEFAULT 0,
    utility_count        INT NOT NULL DEFAULT 0,
    util_damage          INT NOT NULL DEFAULT 0,
    util_successes       INT NOT NULL DEFAULT 0,
    util_enemies         INT NOT NULL DEFAULT 0,
    flash_count          INT NOT NULL DEFAULT 0,
    flash_successes      INT NOT NULL DEFAULT 0,
    hp_removed_total     INT NOT NULL DEFAULT 0,
    hp_dealt_total       INT NOT NULL DEFAULT 0,
    shots_fired          INT NOT NULL DEFAULT 0,
    shots_on_target      INT NOT NULL DEFAULT 0,
    v1_count             INT NOT NULL DEFAULT 0,
    v1_wins              INT NOT NULL DEFAULT 0,
    v2_count             INT NOT NULL DEFAULT 0,
    v2_wins              INT NOT NULL DEFAULT 0,
    v3_count             INT NOT NULL DEFAULT 0,
    v3_wins              INT NOT NULL DEFAULT 0,
    v4_count             INT NOT NULL DEFAULT 0,
    v4_wins              INT NOT NULL DEFAULT 0,
    v5_count             INT NOT NULL DEFAULT 0,
    v5_wins              INT NOT NULL DEFAULT 0,
    entry_count          INT NOT NULL DEFAULT 0,
    entry_wins           INT NOT NULL DEFAULT 0,
    equipment_value      INT NOT NULL DEFAULT 0,
    money_saved          INT NOT NULL DEFAULT 0,
    kill_reward          INT NOT NULL DEFAULT 0,
    live_time            INT NOT NULL DEFAULT 0,
    headshot_kills       INT NOT NULL DEFAULT 0,
    cash_earned          INT NOT NULL DEFAULT 0,
    enemies_flashed      INT NOT NULL DEFAULT 0,
    bomb_plants          INT NOT NULL DEFAULT 0,
    bomb_defuses         INT NOT NULL DEFAULT 0,
    rounds_played        INT NOT NULL DEFAULT 0,
    last_round           INT NOT NULL DEFAULT 0,
    is_closed            TINYINT(1) NOT NULL DEFAULT 0,
    mvps                 INT NOT NULL DEFAULT 0,
    updated_at           DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    UNIQUE KEY uq_match_player (match_id, steamid),
    INDEX idx_match_id (match_id)
) COLLATE=utf8mb4_unicode_ci",

            @"CREATE TABLE IF NOT EXISTS chicken_kills (
    id               BIGINT AUTO_INCREMENT PRIMARY KEY,
    match_id         VARCHAR(64) NOT NULL,
    round            INT NOT NULL,
    killer_steamid   BIGINT UNSIGNED NOT NULL,
    killer_name      VARCHAR(128),
    killer_team      TINYINT,
    weapon           VARCHAR(64),
    created_at       DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    INDEX idx_match_id (match_id)
)",

            // match_rounds: one row per round, keyed by lobby_id + round_number.
            @"CREATE TABLE IF NOT EXISTS match_rounds (
    id           BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    lobby_id     BIGINT UNSIGNED NOT NULL,
    round_number SMALLINT UNSIGNED NOT NULL,
    winner       VARCHAR(10) NOT NULL,
    reason_code  SMALLINT UNSIGNED NOT NULL DEFAULT 0,
    map          VARCHAR(255),
    team1_score  SMALLINT UNSIGNED NOT NULL DEFAULT 0,
    team2_score  SMALLINT UNSIGNED NOT NULL DEFAULT 0,
    half         VARCHAR(10) NOT NULL DEFAULT 'first',
    team1_is_ct  TINYINT(1) NOT NULL DEFAULT 1,
    created_at   TIMESTAMP NULL,
    updated_at   TIMESTAMP NULL,
    UNIQUE KEY uq_lobby_round (lobby_id, round_number),
    INDEX idx_lobby_id (lobby_id)
) COLLATE=utf8mb4_unicode_ci",

            // match_round_players: per-player stats for each round.
            @"CREATE TABLE IF NOT EXISTS match_round_players (
    id              BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
    lobby_id        BIGINT UNSIGNED NOT NULL,
    round_number    SMALLINT UNSIGNED NOT NULL,
    steam_id        VARCHAR(32) NOT NULL,
    name            VARCHAR(255) NOT NULL,
    team            VARCHAR(10) NOT NULL,
    kills           SMALLINT UNSIGNED NOT NULL DEFAULT 0,
    deaths          SMALLINT UNSIGNED NOT NULL DEFAULT 0,
    damage          SMALLINT UNSIGNED NOT NULL DEFAULT 0,
    headshot_kills  SMALLINT UNSIGNED NOT NULL DEFAULT 0,
    assists         INT UNSIGNED NOT NULL DEFAULT 0,
    equipment_value INT UNSIGNED NOT NULL DEFAULT 0,
    money_spent     INT UNSIGNED NOT NULL DEFAULT 0,
    cash_earned     INT UNSIGNED NOT NULL DEFAULT 0,
    created_at      TIMESTAMP NULL,
    updated_at      TIMESTAMP NULL,
    UNIQUE KEY uq_lobby_round_player (lobby_id, round_number, steam_id),
    INDEX idx_lobby_id (lobby_id)
) COLLATE=utf8mb4_unicode_ci"
        };

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            foreach (var sql in statements)
            {
                await using var cmd = new MySqlCommand(sql, conn);
                await cmd.ExecuteNonQueryAsync();
            }
            _initialized = true;
            Console.WriteLine("[CS2Match] Database tables initialized");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CS2Match] Failed to initialize DB tables: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------------
    // users table access control
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns true if the given SteamID exists in the <c>users</c> table
    /// (field: steam_id). Called in AIM mode to gate server access.
    /// Fails open (returns true) when the DB is not yet initialised or
    /// an error occurs so a transient DB hiccup never bricks the server.
    /// </summary>
    public async Task<bool> IsPlayerAllowedAsync(ulong steamId)
    {
        if (!_initialized || _dataSource == null) return true;
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var cmd  = new MySqlCommand(
                "SELECT COUNT(1) FROM users WHERE steam_id = @sid", conn);
            cmd.Parameters.AddWithValue("@sid", steamId.ToString());
            var result = await cmd.ExecuteScalarAsync();
            return Convert.ToInt64(result) > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CS2Match] DB IsPlayerAllowed: {ex.Message}");
            return true; // fail open on error
        }
    }

    // -------------------------------------------------------------------------
    // match_events
    // -------------------------------------------------------------------------

    public async Task LogMatchEventAsync(string matchId, string eventType, string? jsonData = null)
    {
        if (!_initialized || _dataSource == null) return;
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var cmd = new MySqlCommand(
                "INSERT INTO match_events (match_id, event_type, data) VALUES (@mid, @et, @data)", conn);
            cmd.Parameters.AddWithValue("@mid",  matchId);
            cmd.Parameters.AddWithValue("@et",   eventType);
            cmd.Parameters.AddWithValue("@data", (object?)jsonData ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex) { Console.WriteLine($"[CS2Match] DB LogMatchEvent: {ex.Message}"); }
    }

    // -------------------------------------------------------------------------
    // matches
    // -------------------------------------------------------------------------

    public async Task CreateMatchAsync(MatchRow row)
    {
        if (!_initialized || _dataSource == null) return;
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var cmd = new MySqlCommand(@"
INSERT INTO matches (match_id, lobby_id, team1_name, team2_name, map_name, num_maps, status)
VALUES (@mid, @lid, @t1n, @t2n, @map, @nm, 'warmup')
ON DUPLICATE KEY UPDATE
  lobby_id=VALUES(lobby_id), team1_name=VALUES(team1_name), team2_name=VALUES(team2_name),
  map_name=VALUES(map_name), num_maps=VALUES(num_maps), status='warmup',
  team1_score=0, team2_score=0, finished_at=NULL", conn);
            cmd.Parameters.AddWithValue("@mid", row.MatchId);
            cmd.Parameters.AddWithValue("@lid", row.LobbyId);
            cmd.Parameters.AddWithValue("@t1n", row.Team1Name);
            cmd.Parameters.AddWithValue("@t2n", row.Team2Name);
            cmd.Parameters.AddWithValue("@map", row.MapName);
            cmd.Parameters.AddWithValue("@nm",  row.NumMaps);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex) { Console.WriteLine($"[CS2Match] DB CreateMatch: {ex.Message}"); }
    }

    public async Task UpdateMatchAsync(string matchId, int team1Score, int team2Score,
                                       string status, string mapName, int currentMap)
    {
        if (!_initialized || _dataSource == null) return;
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var cmd = new MySqlCommand(@"
UPDATE matches SET
  team1_score=@t1, team2_score=@t2, status=@st, map_name=@map, current_map=@cm
WHERE match_id=@mid", conn);
            cmd.Parameters.AddWithValue("@mid", matchId);
            cmd.Parameters.AddWithValue("@t1",  team1Score);
            cmd.Parameters.AddWithValue("@t2",  team2Score);
            cmd.Parameters.AddWithValue("@st",  status);
            cmd.Parameters.AddWithValue("@map", mapName);
            cmd.Parameters.AddWithValue("@cm",  currentMap);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex) { Console.WriteLine($"[CS2Match] DB UpdateMatch: {ex.Message}"); }
    }

    public async Task FinishMatchAsync(string matchId, int team1Score, int team2Score)
    {
        if (!_initialized || _dataSource == null) return;
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var cmd = new MySqlCommand(@"
UPDATE matches SET
  team1_score=@t1, team2_score=@t2, status='finished', finished_at=CURRENT_TIMESTAMP
WHERE match_id=@mid", conn);
            cmd.Parameters.AddWithValue("@mid", matchId);
            cmd.Parameters.AddWithValue("@t1",  team1Score);
            cmd.Parameters.AddWithValue("@t2",  team2Score);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex) { Console.WriteLine($"[CS2Match] DB FinishMatch: {ex.Message}"); }
    }

    // -------------------------------------------------------------------------
    // kill_events
    // -------------------------------------------------------------------------

    public async Task LogKillAsync(KillEventData d)
    {
        if (!_initialized || _dataSource == null) return;
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var cmd = new MySqlCommand(@"
INSERT INTO kill_events
  (match_id, round,
   attacker_steamid, attacker_name, attacker_team,
   victim_steamid,   victim_name,   victim_team,
   assister_steamid, assister_name, assisted_flash,
   weapon, headshot, thru_smoke, attacker_blind, no_scope, penetrated, attacker_in_air,
   dmg_health, dmg_armor,
   attacker_x, attacker_y, attacker_z,
   victim_x,   victim_y,   victim_z,
   after_time_is_out, part_of_body)
VALUES
  (@mid, @round,
   @asid, @aname, @ateam,
   @vsid, @vname, @vteam,
   @xsid, @xname, @xflash,
   @weapon, @hs, @smoke, @blind, @noscope, @pen, @air,
   @dmgh, @dmga,
   @ax, @ay, @az,
   @vx, @vy, @vz,
   @aftertime, @partofbody)", conn);

            cmd.Parameters.AddWithValue("@mid",    d.MatchId);
            cmd.Parameters.AddWithValue("@round",  d.Round);
            cmd.Parameters.AddWithValue("@asid",   (object?)d.AttackerSteamId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@aname",  (object?)d.AttackerName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ateam",  (object?)d.AttackerTeam ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@vsid",   d.VictimSteamId);
            cmd.Parameters.AddWithValue("@vname",  (object?)d.VictimName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@vteam",  (object?)d.VictimTeam ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@xsid",   (object?)d.AssisterSteamId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@xname",  (object?)d.AssisterName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@xflash", d.AssistedFlash ? 1 : 0);
            cmd.Parameters.AddWithValue("@weapon", (object?)d.Weapon ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@hs",     d.Headshot ? 1 : 0);
            cmd.Parameters.AddWithValue("@smoke",  d.ThruSmoke ? 1 : 0);
            cmd.Parameters.AddWithValue("@blind",  d.AttackerBlind ? 1 : 0);
            cmd.Parameters.AddWithValue("@noscope",d.NoScope ? 1 : 0);
            cmd.Parameters.AddWithValue("@pen",    d.Penetrated ? 1 : 0);
            cmd.Parameters.AddWithValue("@air",    d.AttackerInAir ? 1 : 0);
            cmd.Parameters.AddWithValue("@dmgh",   d.DmgHealth);
            cmd.Parameters.AddWithValue("@dmga",   d.DmgArmor);
            cmd.Parameters.AddWithValue("@ax",     (object?)d.AttackerX ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ay",     (object?)d.AttackerY ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@az",     (object?)d.AttackerZ ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@vx",        (object?)d.VictimX ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@vy",        (object?)d.VictimY ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@vz",        (object?)d.VictimZ ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@aftertime",  d.AfterTimeIsOut ? 1 : 0);
            cmd.Parameters.AddWithValue("@partofbody", (object?)d.PartOfBody ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex) { Console.WriteLine($"[CS2Match] DB LogKill: {ex.Message}"); }
    }

    // -------------------------------------------------------------------------
    // chat_events
    // -------------------------------------------------------------------------

    public async Task LogChatAsync(ChatEventData d)
    {
        if (!_initialized || _dataSource == null) return;
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var cmd = new MySqlCommand(@"
INSERT INTO chat_events (match_id, round, steamid, player_name, message, team_only)
VALUES (@mid, @round, @sid, @pname, @msg, @team)", conn);
            cmd.Parameters.AddWithValue("@mid",   d.MatchId);
            cmd.Parameters.AddWithValue("@round", d.Round);
            cmd.Parameters.AddWithValue("@sid",   d.SteamId);
            cmd.Parameters.AddWithValue("@pname", (object?)d.PlayerName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@msg",   d.Message);
            cmd.Parameters.AddWithValue("@team",  d.TeamOnly ? 1 : 0);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex) { Console.WriteLine($"[CS2Match] DB LogChat: {ex.Message}"); }
    }

    // -------------------------------------------------------------------------
    // round_events
    // -------------------------------------------------------------------------

    public async Task LogRoundEventAsync(RoundEventData d)
    {
        if (!_initialized || _dataSource == null) return;
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var cmd = new MySqlCommand(@"
INSERT INTO round_events (match_id, round, event_type, team1_score, team2_score, winner_team, data)
VALUES (@mid, @round, @et, @t1, @t2, @winner, @data)", conn);
            cmd.Parameters.AddWithValue("@mid",    d.MatchId);
            cmd.Parameters.AddWithValue("@round",  d.Round);
            cmd.Parameters.AddWithValue("@et",     d.EventType);
            cmd.Parameters.AddWithValue("@t1",     d.Team1Score);
            cmd.Parameters.AddWithValue("@t2",     d.Team2Score);
            cmd.Parameters.AddWithValue("@winner", (object?)d.WinnerTeam ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@data",   (object?)d.JsonData ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex) { Console.WriteLine($"[CS2Match] DB LogRoundEvent: {ex.Message}"); }
    }

    // -------------------------------------------------------------------------
    // match_scoreboard
    // -------------------------------------------------------------------------

    public async Task UpsertScoreboardAsync(ScoreboardRow r)
    {
        if (!_initialized || _dataSource == null) return;
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var cmd = new MySqlCommand(@"
INSERT INTO match_scoreboard
  (match_id, steamid, player_name, config_team, team_name,
   kills, deaths, damage_dealt, assists,
   kills_5k, kills_4k, kills_3k, kills_2k,
   utility_count, util_damage, util_successes, util_enemies,
   flash_count, flash_successes,
   hp_removed_total, hp_dealt_total,
   shots_fired, shots_on_target,
   v1_count, v1_wins, v2_count, v2_wins,
   v3_count, v3_wins, v4_count, v4_wins, v5_count, v5_wins,
   entry_count, entry_wins,
   equipment_value, money_saved, kill_reward, live_time,
   headshot_kills, cash_earned, enemies_flashed,
   bomb_plants, bomb_defuses, rounds_played, last_round, mvps)
VALUES
  (@mid,@sid,@name,@ct,@tn,
   @k,@d,@dmg,@a,
   @k5,@k4,@k3,@k2,
   @uc,@udmg,@us,@ue,
   @fc,@fs,
   @hprem,@hpdlt,
   @sf,@sot,
   @v1c,@v1w,@v2c,@v2w,
   @v3c,@v3w,@v4c,@v4w,@v5c,@v5w,
   @enc,@enw,
   @eqv,@ms,@kr,@lt,
   @hsk,@ce,@ef,
   @bp,@bd,@rp,@lr,@mvp)
ON DUPLICATE KEY UPDATE
  player_name=VALUES(player_name),
  kills=VALUES(kills), deaths=VALUES(deaths), damage_dealt=VALUES(damage_dealt), assists=VALUES(assists),
  kills_5k=VALUES(kills_5k), kills_4k=VALUES(kills_4k), kills_3k=VALUES(kills_3k), kills_2k=VALUES(kills_2k),
  utility_count=VALUES(utility_count), util_damage=VALUES(util_damage),
  util_successes=VALUES(util_successes), util_enemies=VALUES(util_enemies),
  flash_count=VALUES(flash_count), flash_successes=VALUES(flash_successes),
  hp_removed_total=VALUES(hp_removed_total), hp_dealt_total=VALUES(hp_dealt_total),
  shots_fired=VALUES(shots_fired), shots_on_target=VALUES(shots_on_target),
  v1_count=VALUES(v1_count), v1_wins=VALUES(v1_wins),
  v2_count=VALUES(v2_count), v2_wins=VALUES(v2_wins),
  v3_count=VALUES(v3_count), v3_wins=VALUES(v3_wins),
  v4_count=VALUES(v4_count), v4_wins=VALUES(v4_wins),
  v5_count=VALUES(v5_count), v5_wins=VALUES(v5_wins),
  entry_count=VALUES(entry_count), entry_wins=VALUES(entry_wins),
  equipment_value=VALUES(equipment_value), money_saved=VALUES(money_saved),
  kill_reward=VALUES(kill_reward), live_time=VALUES(live_time),
  headshot_kills=VALUES(headshot_kills), cash_earned=VALUES(cash_earned), enemies_flashed=VALUES(enemies_flashed),
  bomb_plants=VALUES(bomb_plants), bomb_defuses=VALUES(bomb_defuses),
  rounds_played=VALUES(rounds_played), last_round=VALUES(last_round),
  mvps=VALUES(mvps),
  updated_at=CURRENT_TIMESTAMP", conn);

            cmd.Parameters.AddWithValue("@mid",  r.MatchId);
            cmd.Parameters.AddWithValue("@sid",  r.SteamId);
            cmd.Parameters.AddWithValue("@name", (object?)r.PlayerName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ct",   r.ConfigTeam);
            cmd.Parameters.AddWithValue("@tn",   r.TeamName ?? "");
            cmd.Parameters.AddWithValue("@k",    r.Kills);
            cmd.Parameters.AddWithValue("@d",    r.Deaths);
            cmd.Parameters.AddWithValue("@dmg",  r.DamageDealt);
            cmd.Parameters.AddWithValue("@a",    r.Assists);
            cmd.Parameters.AddWithValue("@k5",   r.Kills5k);
            cmd.Parameters.AddWithValue("@k4",   r.Kills4k);
            cmd.Parameters.AddWithValue("@k3",   r.Kills3k);
            cmd.Parameters.AddWithValue("@k2",   r.Kills2k);
            cmd.Parameters.AddWithValue("@uc",   r.UtilityCount);
            cmd.Parameters.AddWithValue("@udmg", r.UtilDamage);
            cmd.Parameters.AddWithValue("@us",   r.UtilSuccesses);
            cmd.Parameters.AddWithValue("@ue",   r.UtilEnemiesHit);
            cmd.Parameters.AddWithValue("@fc",   r.FlashCount);
            cmd.Parameters.AddWithValue("@fs",   r.FlashSuccesses);
            cmd.Parameters.AddWithValue("@hprem",r.HealthPointsRemovedTotal);
            cmd.Parameters.AddWithValue("@hpdlt",r.HealthPointsDealtTotal);
            cmd.Parameters.AddWithValue("@sf",   r.ShotsFired);
            cmd.Parameters.AddWithValue("@sot",  r.ShotsOnTarget);
            cmd.Parameters.AddWithValue("@v1c",  r.V1Count);
            cmd.Parameters.AddWithValue("@v1w",  r.V1Wins);
            cmd.Parameters.AddWithValue("@v2c",  r.V2Count);
            cmd.Parameters.AddWithValue("@v2w",  r.V2Wins);
            cmd.Parameters.AddWithValue("@v3c",  r.V3Count);
            cmd.Parameters.AddWithValue("@v3w",  r.V3Wins);
            cmd.Parameters.AddWithValue("@v4c",  r.V4Count);
            cmd.Parameters.AddWithValue("@v4w",  r.V4Wins);
            cmd.Parameters.AddWithValue("@v5c",  r.V5Count);
            cmd.Parameters.AddWithValue("@v5w",  r.V5Wins);
            cmd.Parameters.AddWithValue("@enc",  r.EntryCount);
            cmd.Parameters.AddWithValue("@enw",  r.EntryWins);
            cmd.Parameters.AddWithValue("@eqv",  r.EquipmentValue);
            cmd.Parameters.AddWithValue("@ms",   r.MoneySaved);
            cmd.Parameters.AddWithValue("@kr",   r.KillReward);
            cmd.Parameters.AddWithValue("@lt",   r.LiveTime);
            cmd.Parameters.AddWithValue("@hsk",  r.Headshots);
            cmd.Parameters.AddWithValue("@ce",   r.CashEarned);
            cmd.Parameters.AddWithValue("@ef",   r.EnemiesFlashed);
            cmd.Parameters.AddWithValue("@bp",   r.BombPlants);
            cmd.Parameters.AddWithValue("@bd",   r.BombDefuses);
            cmd.Parameters.AddWithValue("@rp",   r.RoundsPlayed);
            cmd.Parameters.AddWithValue("@lr",   r.LastRound);
            cmd.Parameters.AddWithValue("@mvp",  r.Mvps);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex) { Console.WriteLine($"[CS2Match] DB UpsertScoreboard: {ex.Message}"); }
    }

    public async Task CloseScoreboardAsync(string matchId)
    {
        if (!_initialized || _dataSource == null) return;
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var cmd = new MySqlCommand(
                "UPDATE match_scoreboard SET is_closed=1, updated_at=CURRENT_TIMESTAMP WHERE match_id=@mid", conn);
            cmd.Parameters.AddWithValue("@mid", matchId);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex) { Console.WriteLine($"[CS2Match] DB CloseScoreboard: {ex.Message}"); }
    }

    // -------------------------------------------------------------------------
    // match_rounds
    // -------------------------------------------------------------------------

    public async Task InsertMatchRoundAsync(MatchRoundRow r)
    {
        if (!_initialized || _dataSource == null) return;
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var cmd = new MySqlCommand(@"
INSERT INTO match_rounds
  (lobby_id, round_number, winner, reason_code, map, team1_score, team2_score, half, team1_is_ct, created_at, updated_at)
VALUES
  (@lid, @rnum, @winner, @reason, @map, @t1, @t2, @half, @t1ct, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
ON DUPLICATE KEY UPDATE
  winner=VALUES(winner), reason_code=VALUES(reason_code),
  team1_score=VALUES(team1_score), team2_score=VALUES(team2_score),
  half=VALUES(half), team1_is_ct=VALUES(team1_is_ct),
  updated_at=CURRENT_TIMESTAMP", conn);
            cmd.Parameters.AddWithValue("@lid",    r.LobbyId);
            cmd.Parameters.AddWithValue("@rnum",   r.RoundNumber);
            cmd.Parameters.AddWithValue("@winner", r.Winner);
            cmd.Parameters.AddWithValue("@reason", r.ReasonCode);
            cmd.Parameters.AddWithValue("@map",    (object?)r.Map ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@t1",     r.Team1Score);
            cmd.Parameters.AddWithValue("@t2",     r.Team2Score);
            cmd.Parameters.AddWithValue("@half",   r.Half);
            cmd.Parameters.AddWithValue("@t1ct",   r.Team1IsCt ? 1 : 0);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex) { Console.WriteLine($"[CS2Match] DB InsertMatchRound: {ex.Message}"); }
    }

    // -------------------------------------------------------------------------
    // lobbies
    // -------------------------------------------------------------------------

    public async Task FinishLobbyAsync(ulong lobbyId, string demoName)
    {
        if (!_initialized || _dataSource == null) return;
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var cmd = new MySqlCommand(@"
UPDATE lobbies SET status='finished', demo_name=@demo, updated_at=CURRENT_TIMESTAMP
WHERE id=@lid", conn);
            cmd.Parameters.AddWithValue("@lid",  lobbyId);
            cmd.Parameters.AddWithValue("@demo", demoName);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex) { Console.WriteLine($"[CS2Match] DB FinishLobby: {ex.Message}"); }
    }

    // -------------------------------------------------------------------------
    // match_round_players
    // -------------------------------------------------------------------------

    public async Task InsertRoundPlayersAsync(IEnumerable<RoundPlayerRow> rows)
    {
        if (!_initialized || _dataSource == null) return;
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            foreach (var r in rows)
            {
                await using var cmd = new MySqlCommand(@"
INSERT INTO match_round_players
  (lobby_id, round_number, steam_id, name, team,
   kills, deaths, damage, headshot_kills, assists, equipment_value,
   money_spent, cash_earned,
   created_at, updated_at)
VALUES
  (@lid, @rnum, @sid, @name, @team,
   @k, @d, @dmg, @hsk, @a, @ev,
   @ms, @ce,
   CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
ON DUPLICATE KEY UPDATE
  kills=VALUES(kills), deaths=VALUES(deaths), damage=VALUES(damage),
  headshot_kills=VALUES(headshot_kills), assists=VALUES(assists),
  equipment_value=VALUES(equipment_value),
  money_spent=VALUES(money_spent), cash_earned=VALUES(cash_earned),
  updated_at=CURRENT_TIMESTAMP", conn);
                cmd.Parameters.AddWithValue("@lid",  r.LobbyId);
                cmd.Parameters.AddWithValue("@rnum", r.RoundNumber);
                cmd.Parameters.AddWithValue("@sid",  r.SteamId);
                cmd.Parameters.AddWithValue("@name", r.Name);
                cmd.Parameters.AddWithValue("@team", r.Team);
                cmd.Parameters.AddWithValue("@k",    r.Kills);
                cmd.Parameters.AddWithValue("@d",    r.Deaths);
                cmd.Parameters.AddWithValue("@dmg",  r.Damage);
                cmd.Parameters.AddWithValue("@hsk",  r.HeadshotKills);
                cmd.Parameters.AddWithValue("@a",    r.Assists);
                cmd.Parameters.AddWithValue("@ev",   r.EquipmentValue);
                cmd.Parameters.AddWithValue("@ms",   r.MoneySpent);
                cmd.Parameters.AddWithValue("@ce",   r.CashEarned);
                await cmd.ExecuteNonQueryAsync();
            }
        }
        catch (Exception ex) { Console.WriteLine($"[CS2Match] DB InsertRoundPlayers: {ex.Message}"); }
    }

    // -------------------------------------------------------------------------
    // chicken_kills
    // -------------------------------------------------------------------------

    public async Task LogChickenKillAsync(ChickenKillData d)
    {
        if (!_initialized || _dataSource == null) return;
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync();
            await using var cmd = new MySqlCommand(@"
INSERT INTO chicken_kills (match_id, round, killer_steamid, killer_name, killer_team, weapon)
VALUES (@mid, @round, @sid, @name, @team, @weapon)", conn);
            cmd.Parameters.AddWithValue("@mid",    d.MatchId);
            cmd.Parameters.AddWithValue("@round",  d.Round);
            cmd.Parameters.AddWithValue("@sid",    d.KillerSteamId);
            cmd.Parameters.AddWithValue("@name",   (object?)d.KillerName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@team",   (object?)d.KillerTeam ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@weapon", (object?)d.Weapon ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex) { Console.WriteLine($"[CS2Match] DB LogChickenKill: {ex.Message}"); }
    }
}

// -------------------------------------------------------------------------
// Records
// -------------------------------------------------------------------------

public record MatchRow(
    string MatchId,
    string LobbyId,
    string Team1Name,
    string Team2Name,
    string MapName,
    int NumMaps
);

public record KillEventData(
    string MatchId,
    int Round,
    ulong? AttackerSteamId,
    string? AttackerName,
    int? AttackerTeam,
    ulong VictimSteamId,
    string? VictimName,
    int? VictimTeam,
    ulong? AssisterSteamId,
    string? AssisterName,
    bool AssistedFlash,
    string? Weapon,
    bool Headshot,
    bool ThruSmoke,
    bool AttackerBlind,
    bool NoScope,
    bool Penetrated,
    bool AttackerInAir,
    int DmgHealth,
    int DmgArmor,
    float? AttackerX, float? AttackerY, float? AttackerZ,
    float? VictimX,   float? VictimY,   float? VictimZ,
    bool AfterTimeIsOut,
    int? PartOfBody
);

public record ChatEventData(
    string MatchId,
    int Round,
    ulong SteamId,
    string? PlayerName,
    string Message,
    bool TeamOnly
);

public record RoundEventData(
    string MatchId,
    int Round,
    string EventType,
    int Team1Score,
    int Team2Score,
    int? WinnerTeam,
    string? JsonData = null
);

public record ScoreboardRow(
    string MatchId,
    ulong SteamId,
    string? PlayerName,
    int ConfigTeam,
    string? TeamName,
    // Core
    int Kills,
    int Deaths,
    int DamageDealt,
    int Assists,
    // Multi-kills
    int Kills5k,
    int Kills4k,
    int Kills3k,
    int Kills2k,
    // Utility
    int UtilityCount,   // total grenades thrown
    int UtilDamage,
    int UtilSuccesses,  // util grenades that hit ≥1 enemy
    int UtilEnemiesHit, // total enemies hit by util
    // Flash
    int FlashCount,
    int FlashSuccesses,
    // Engine HP totals
    int HealthPointsRemovedTotal,
    int HealthPointsDealtTotal,
    // Shots
    int ShotsFired,
    int ShotsOnTarget,
    // Clutch
    int V1Count, int V1Wins,
    int V2Count, int V2Wins,
    int V3Count, int V3Wins,
    int V4Count, int V4Wins,
    int V5Count, int V5Wins,
    // Entry
    int EntryCount, int EntryWins,
    // Economy
    int EquipmentValue,
    int MoneySaved,
    int KillReward,
    int LiveTime,       // seconds alive (int, from engine)
    // Kill quality
    int Headshots,
    int CashEarned,
    int EnemiesFlashed,
    // Bomb
    int BombPlants,
    int BombDefuses,
    // Tracking
    int RoundsPlayed,
    int LastRound,
    int Mvps
);

public record RoundPlayerRow(
    ulong LobbyId,
    int RoundNumber,
    string SteamId,
    string Name,
    string Team,
    int Kills,
    int Deaths,
    int Damage,
    int HeadshotKills,
    int Assists,
    int EquipmentValue,
    int MoneySpent,
    int CashEarned
);

public record ChickenKillData(
    string MatchId,
    int Round,
    ulong KillerSteamId,
    string? KillerName,
    int? KillerTeam,
    string? Weapon
);

public record MatchRoundRow(
    ulong LobbyId,
    int RoundNumber,
    string Winner,      // "team1" or "team2"
    int ReasonCode,     // EventRoundEnd.Reason
    string? Map,
    int Team1Score,
    int Team2Score,
    string Half,        // "first", "second", or "overtime"
    bool Team1IsCt
);
