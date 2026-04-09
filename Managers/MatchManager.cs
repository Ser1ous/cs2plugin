using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CS2MatchPlugin.Config;
using CS2MatchPlugin.Services;
using CS2MatchPlugin.State;

namespace CS2MatchPlugin.Managers;

public class MatchManager
{
    private readonly ConfigDownloader _downloader;
    private readonly MapChanger _mapChanger;
    private readonly CfgExecutor _cfgExecutor;
    private readonly ReadyManager _readyManager;
    private readonly PauseManager _pauseManager;
    private readonly KnifeManager _knifeManager;
    private readonly AimManager _aimManager;
    private readonly DatabaseService _db;
    private readonly PluginConfig _pluginConfig;
    private readonly BasePlugin _plugin;

    public MatchContext? Context { get; private set; }

    public MatchManager(
        ConfigDownloader downloader,
        MapChanger mapChanger,
        CfgExecutor cfgExecutor,
        ReadyManager readyManager,
        PauseManager pauseManager,
        KnifeManager knifeManager,
        AimManager aimManager,
        DatabaseService db,
        PluginConfig pluginConfig,
        BasePlugin plugin)
    {
        _downloader   = downloader;
        _mapChanger   = mapChanger;
        _cfgExecutor  = cfgExecutor;
        _readyManager = readyManager;
        _pauseManager = pauseManager;
        _knifeManager = knifeManager;
        _aimManager   = aimManager;
        _db           = db;
        _pluginConfig = pluginConfig;
        _plugin       = plugin;

        _readyManager.OnAllReady         += StartKnifeRound;
        _pauseManager.OnBothTeamsUnpaused += HandleUnpause;
        _knifeManager.OnKnifeRoundWinner  += HandleKnifeWinner;
    }

    public MatchState State => Context?.State ?? MatchState.None;

    // -------------------------------------------------------------------------
    // Load match from URL
    // -------------------------------------------------------------------------

    public async Task LoadMatchFromUrlAsync(string url, Action<string> feedback)
    {
        feedback("Downloading match config...");
        MatchConfig config;
        try
        {
            config = await _downloader.DownloadAsync(url);
        }
        catch (Exception ex)
        {
            feedback($"Error loading config: {ex.Message}");
            return;
        }

        Server.NextFrame(() =>
        {
            AbortMatch(silent: true);

            Context = new MatchContext
            {
                Config = config,
                State  = MatchState.Warmup,
                // Default sides before knife: Team1=T, Team2=CT.
                // Overridden after knife winner picks a side.
                Team1Side = TeamSide.Terrorist,
                Team2Side = TeamSide.CounterTerrorist,
            };
            _readyManager.Setup(config);
            _pauseManager.Setup(config);

            feedback($"Loaded: {config.MatchId} | {config.Team1.Name} vs {config.Team2.Name} | Map: {config.Maplist[0]}");
            _ = _db.LogMatchEventAsync(config.MatchId, "match_loaded", $"{{\"url\":\"{url}\"}}");
            _ = _db.CreateMatchAsync(new MatchRow(
                config.MatchId,
                config.LobbyId,
                config.Team1.Name,
                config.Team2.Name,
                config.Maplist[0],
                config.NumMaps
            ));

            string targetMap = config.Maplist[0];
            bool alreadyOnMap = string.Equals(Server.MapName, targetMap, StringComparison.OrdinalIgnoreCase);

            if (alreadyOnMap)
            {
                // changelevel to the same map causes queue overflow disconnects.
                // Use mp_restartgame instead. OnMapStart will NOT fire, so we set
                // PendingWarmup=true manually — OnFirstRoundStart will pick it up.
                Context.PendingWarmup = true;
            }

            // game_type/game_mode are passed into ChangeMap so they are set
            // immediately before changelevel/mp_restartgame — CS2 reads them
            // right then in ExecGameTypeCfg to pick the correct scoreboard/HUD.
            _mapChanger.ChangeMap(targetMap, _pluginConfig.GameType, _pluginConfig.GameMode);
        });
    }

    // -------------------------------------------------------------------------
    // Map lifecycle
    // -------------------------------------------------------------------------

    /// <summary>
    /// Called at the very start of map load — the server is NOT ready yet.
    /// Do NOT call Server.ExecuteCommand or access game objects here.
    /// Only set flags; actual warmup is deferred to OnFirstRoundStart.
    /// </summary>
    public void OnMapStart(string mapName)
    {
        if (Context == null)
        {
            // AIM mode: flag pending so OnFirstRoundStart applies the aim cfg
            Console.WriteLine($"[CS2Match] Map starting (AIM mode): {mapName}");
            return;
        }

        string expected = Context.Config.Maplist[Context.CurrentMapIndex];
        if (!string.Equals(mapName, expected, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"[CS2Match] OnMapStart: got '{mapName}', expected '{expected}', ignoring");
            return;
        }

        // mp_restartgame on the same map triggers OnMapStart but we must NOT
        // reset state when a live match is already in progress (e.g. knife→live restart).
        if (Context.State == MatchState.Live || Context.State == MatchState.Paused)
        {
            Console.WriteLine($"[CS2Match] OnMapStart during {Context.State} — skipping warmup reset");
            return;
        }

        // Reset sides to default for new map — knife round will reassign them
        Context.Team1Side = TeamSide.Terrorist;
        Context.Team2Side = TeamSide.CounterTerrorist;

        // Capture timestamp now — tv_autorecord names the demo file using the
        // time the map loaded, not the time the match goes live.
        Context.DemoTimestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmm");

        // Mark warmup as pending — actual setup runs on first RoundStart
        Context.PendingWarmup = true;
        Console.WriteLine($"[CS2Match] Map loading: {mapName} — warmup will start on first round");
    }

    /// <summary>
    /// Called on every RoundStart. Handles deferred warmup setup and AIM cfg
    /// application — both need the server to be fully active first.
    /// </summary>
    public void OnFirstRoundStart(string currentMapName)
    {
        // AIM mode
        if (Context == null)
        {
            if (_aimManager.IsAimMap(currentMapName))
                _aimManager.ApplyAimConfig();
            return;
        }

        // Match waiting for warmup
        if (Context.PendingWarmup)
        {
            Context.PendingWarmup = false;
            EnterWarmup();
        }
    }

    // -------------------------------------------------------------------------
    // Warmup
    // -------------------------------------------------------------------------

    private void EnterWarmup()
    {
        if (Context == null) return;
        Context.State = MatchState.Warmup;
        _readyManager.Reset();

        _cfgExecutor.ExecCfg(_pluginConfig.WarmupCfgName);
        Server.ExecuteCommand("mp_warmup_start");

        BroadcastAll($" \x04[Match]\x01 {Context.Config.Team1.Name} vs {Context.Config.Team2.Name}");
        BroadcastAll($" \x04[Match]\x01 Map: \x0B{Context.Config.Maplist[Context.CurrentMapIndex]}\x01 — type \x09!ready\x01 when ready");

        _ = _db.LogMatchEventAsync(Context.Config.MatchId, "warmup_start");
    }

    public void OnPlayerReady(CCSPlayerController player)
    {
        if (Context?.State != MatchState.Warmup) return;

        if (!_readyManager.IsRegisteredPlayer(player.SteamID))
        {
            player.PrintToChat(" \x02[Match]\x01 You are not registered in this match.");
            return;
        }

        bool accepted = _readyManager.MarkReady(player.SteamID);
        if (!accepted) return;

        var (ready, required) = _readyManager.GetStatus();
        BroadcastAll($" \x04[Match]\x01 {player.PlayerName} is ready! \x09{ready}/{required}\x01");

        var notReady = _readyManager.GetNotReadyNames();
        if (notReady.Count > 0)
            BroadcastAll($" \x04[Match]\x01 Waiting for: \x02{string.Join("\x01, \x02", notReady)}\x01");
    }

    // -------------------------------------------------------------------------
    // Knife round
    // -------------------------------------------------------------------------

    private void StartKnifeRound()
    {
        if (Context == null) return;

        string mapSide = Context.CurrentMapIndex < Context.Config.MapSides.Count
            ? Context.Config.MapSides[Context.CurrentMapIndex]
            : "knife";

        if (mapSide != "knife")
        {
            AssignPredeterminedSides(mapSide);
            StartLive();
            return;
        }

        Context.State = MatchState.Knife;
        _cfgExecutor.ExecCfg(_pluginConfig.KnifeCfgName);
        // Match MatchZy's knife.cfg order: restartgame first (1s countdown),
        // then warmup_end (exits warmup immediately). By the time the restart fires
        // warmup has ended and the round starts cleanly with knife settings.
        Server.ExecuteCommand("mp_restartgame 1");
        Server.ExecuteCommand("mp_warmup_end");

        BroadcastAll(" \x04[Match]\x01 \x09KNIFE ROUND\x01 — winner picks side!");
        _ = _db.LogMatchEventAsync(Context.Config.MatchId, "knife_start");
        _ = _db.UpdateMatchAsync(Context.Config.MatchId,
            Context.Team1Score, Context.Team2Score, "knife",
            Context.Config.Maplist[Context.CurrentMapIndex], Context.CurrentMapIndex + 1);
    }

    private void AssignPredeterminedSides(string mapSide)
    {
        if (Context == null) return;
        if (mapSide == "team1_ct")
        {
            Context.Team1Side = TeamSide.CounterTerrorist;
            Context.Team2Side = TeamSide.Terrorist;
        }
        else
        {
            Context.Team1Side = TeamSide.Terrorist;
            Context.Team2Side = TeamSide.CounterTerrorist;
        }
    }

    private void HandleKnifeWinner(TeamSide winnerSide)
    {
        if (Context?.State != MatchState.Knife) return;

        Context.State = MatchState.SidePick;
        Context.KnifeWinnerCsTeam = winnerSide;
        // Resolve NOW while players are still on their knife-round teams (before CS2's own reset shuffles them)
        Context.KnifeWinnerConfigTeam = ResolveConfigTeamBySide(winnerSide);

        string winnerName = Context.KnifeWinnerConfigTeam == 1
            ? Context.Config.Team1.Name
            : Context.Config.Team2.Name;

        _ = _db.LogMatchEventAsync(Context.Config.MatchId, "knife_winner",
            $"{{\"winner_config_team\":{Context.KnifeWinnerConfigTeam},\"side\":\"{winnerSide}\"}}");

        BroadcastAll($" \x04[Match]\x01 \x09{winnerName}\x01 won the knife round!");

        // Broadcast side pick options immediately and pause round 2.
        // knife.cfg has mp_maxrounds 2 so the match doesn't end here.
        // mp_pause_match queues a pause for the start of the next round's freeze time,
        // giving the winner time to pick a side without round 2 actually playing.
        BroadcastSidePickInfo();
        Server.ExecuteCommand("mp_pause_match");
    }

    public void BroadcastSidePickInfo()
    {
        if (Context?.State != MatchState.SidePick) return;

        Console.WriteLine("[CS2Match] Broadcasting side pick info");

        string name = Context.KnifeWinnerConfigTeam == 1
            ? Context.Config.Team1.Name
            : Context.Config.Team2.Name;
        string loserName = Context.KnifeWinnerConfigTeam == 1
            ? Context.Config.Team2.Name
            : Context.Config.Team1.Name;
        string winnerCurrentSide = Context.KnifeWinnerCsTeam == TeamSide.CounterTerrorist ? "CT" : "T";
        string loserCurrentSide  = Context.KnifeWinnerCsTeam == TeamSide.CounterTerrorist ? "T"  : "CT";

        BroadcastAll($" \x04[Match]\x01 {name} is on \x09{winnerCurrentSide}\x01 | {loserName} is on \x09{loserCurrentSide}\x01");
        BroadcastAll($" \x04[Match]\x01 {name}: \x09.stay\x01 = keep {winnerCurrentSide}  |  \x09.switch\x01 = swap to {loserCurrentSide}");
        BroadcastAll($" \x04[Match]\x01 (also accepted: \x09.ct\x01 or \x09.t\x01)");
    }

    /// <summary>
    /// Inspects currently connected players to determine which config team
    /// is currently playing on the given CS side.
    /// </summary>
    private int ResolveConfigTeamBySide(TeamSide side)
    {
        if (Context == null) return 1;
        foreach (var p in Utilities.GetPlayers())
        {
            if (!p.IsValid || p.IsBot) continue;
            string sid = p.SteamID.ToString();
            if (p.TeamNum == (int)side && Context.Config.Team1.Players.ContainsKey(sid)) return 1;
            if (p.TeamNum == (int)side && Context.Config.Team2.Players.ContainsKey(sid)) return 2;
        }
        return 1;
    }

    /// <summary>
    /// Called by player commands: side = "ct", "t", "stay", or "switch".
    /// "stay"   = winner keeps their current knife-round side.
    /// "switch" = winner swaps to the other side.
    /// </summary>
    public void OnSidePick(CCSPlayerController player, string side)
    {
        Console.WriteLine($"[CS2Match] OnSidePick: player={player.PlayerName} side={side} state={Context?.State}");
        if (Context?.State != MatchState.SidePick) return;

        string sid = player.SteamID.ToString();
        bool isTeam1 = Context.Config.Team1.Players.ContainsKey(sid);
        bool isTeam2 = Context.Config.Team2.Players.ContainsKey(sid);

        if (!isTeam1 && !isTeam2)
        {
            player.PrintToChat(" \x02[Match]\x01 You are not in this match.");
            return;
        }

        int playerConfigTeam = isTeam1 ? 1 : 2;

        if (Context.KnifeWinnerConfigTeam == 0)
        {
            // Fallback: first registered player to pick wins (shouldn't normally happen)
            Context.KnifeWinnerConfigTeam = playerConfigTeam;
        }

        if (playerConfigTeam != Context.KnifeWinnerConfigTeam)
        {
            string winnerName = Context.KnifeWinnerConfigTeam == 1
                ? Context.Config.Team1.Name : Context.Config.Team2.Name;
            player.PrintToChat($" \x02[Match]\x01 Only \x09{winnerName}\x01 (knife winner) can pick the side.");
            return;
        }

        // Resolve "stay" / "switch" into concrete CT/T choice
        string resolvedSide = side;
        if (side == "stay")
        {
            // Winner keeps the side they were on during the knife round
            resolvedSide = Context.KnifeWinnerCsTeam == TeamSide.CounterTerrorist ? "ct" : "t";
        }
        else if (side == "switch")
        {
            // Winner swaps to the opposite side
            resolvedSide = Context.KnifeWinnerCsTeam == TeamSide.CounterTerrorist ? "t" : "ct";
        }

        if (resolvedSide == "ct")
        {
            Context.Team1Side = playerConfigTeam == 1 ? TeamSide.CounterTerrorist : TeamSide.Terrorist;
            Context.Team2Side = playerConfigTeam == 1 ? TeamSide.Terrorist : TeamSide.CounterTerrorist;
        }
        else
        {
            Context.Team1Side = playerConfigTeam == 1 ? TeamSide.Terrorist : TeamSide.CounterTerrorist;
            Context.Team2Side = playerConfigTeam == 1 ? TeamSide.CounterTerrorist : TeamSide.Terrorist;
        }

        string name = playerConfigTeam == 1 ? Context.Config.Team1.Name : Context.Config.Team2.Name;
        BroadcastAll($" \x04[Match]\x01 {name} chose to start as \x09{resolvedSide.ToUpper()}\x01!");

        _ = _db.LogMatchEventAsync(Context.Config.MatchId, "side_pick",
            $"{{\"team\":\"{name}\",\"side\":\"{resolvedSide}\"}}");

        StartLiveFromKnife();
    }

    // -------------------------------------------------------------------------
    // Live
    // -------------------------------------------------------------------------

    /// <summary>
    /// Starts the live match from a warmup state (predetermined sides — no knife round).
    /// Uses mp_warmup_end to exit warmup cleanly.
    /// </summary>
    private void StartLive()
    {
        if (Context == null) return;

        Context.State = MatchState.Live;
        Context.Team1Score = 0;
        Context.Team2Score = 0;

        MovePlayers();

        BroadcastAll($" \x04[Match]\x01 {Context.Config.Team1.Name} starts as \x09{SideName(Context.Team1Side)}\x01");
        BroadcastAll($" \x04[Match]\x01 {Context.Config.Team2.Name} starts as \x09{SideName(Context.Team2Side)}\x01");

        // Apply all competitive cvars then exit warmup.
        // mp_warmup_end is called after the cfg so all cvars are set before BeginMatch fires.
        _cfgExecutor.ExecCfg(_pluginConfig.CompetitiveCfgName);
        _cfgExecutor.ExecCvars(Context.Config.Cvars);
        Server.ExecuteCommand("mp_warmup_end");

        BroadcastLive();
        LogLive();
    }

    /// <summary>
    /// Starts the live match from the knife pause state.
    /// Uses mp_restartgame to restart the match cleanly with competitive settings.
    /// The game is currently paused during knife round 2 — mp_restartgame overrides that.
    /// </summary>
    private void StartLiveFromKnife()
    {
        if (Context == null) return;

        Context.State = MatchState.Live;
        Context.Team1Score = 0;
        Context.Team2Score = 0;

        MovePlayers();

        BroadcastAll($" \x04[Match]\x01 {Context.Config.Team1.Name} starts as \x09{SideName(Context.Team1Side)}\x01");
        BroadcastAll($" \x04[Match]\x01 {Context.Config.Team2.Name} starts as \x09{SideName(Context.Team2Side)}\x01");

        // Apply all competitive cvars, then restart from 0-0.
        // mp_unpause_match must come before mp_restartgame — the pause state from
        // mp_pause_match survives a game restart otherwise.
        _cfgExecutor.ExecCfg(_pluginConfig.CompetitiveCfgName);
        _cfgExecutor.ExecCvars(Context.Config.Cvars);
        Server.ExecuteCommand("mp_unpause_match");
        Server.ExecuteCommand("mp_restartgame 3");

        // LIVE messages after restart settles (3s countdown + buffer)
        BroadcastLive(delay: 5f);
        LogLive();
    }

    private void MovePlayers()
    {
        if (Context == null) return;
        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid || player.IsBot) continue;
            string sid = player.SteamID.ToString();
            int targetSide;
            if (Context.Config.Team1.Players.ContainsKey(sid))
                targetSide = (int)Context.Team1Side;
            else if (Context.Config.Team2.Players.ContainsKey(sid))
                targetSide = (int)Context.Team2Side;
            else
                continue;

            if (player.TeamNum != targetSide)
                player.SwitchTeam((CounterStrikeSharp.API.Modules.Utils.CsTeam)targetSide);
        }
    }

    private void BroadcastLive(float delay = 2f)
    {
        _plugin.AddTimer(delay, () =>
        {
            if (Context?.State != MatchState.Live) return;
            BroadcastAll(" \x04[Match]\x01 \x04!!!! LIVE !!!!\x01");
            BroadcastAll(" \x04[Match]\x01 \x04!!!! LIVE !!!!\x01");
            BroadcastAll(" \x04[Match]\x01 \x04!!!! LIVE !!!!\x01");
        });
    }

    private void LogLive()
    {
        if (Context == null) return;
        string matchId = Context.Config.MatchId;
        string map = Context.Config.Maplist[Context.CurrentMapIndex];
        int mapIdx = Context.CurrentMapIndex + 1;

        // Build the demo filename using CS2's auto-record format:
        // auto-YYYYMMDD-HHMM-{map}-{hostname}.dem
        // Timestamp is from map load, matching when tv_autorecord actually started.
        string timestamp = Context.DemoTimestamp;
        string hostname  = ConVar.Find("hostname")?.StringValue ?? "server";
        // Sanitise hostname: strip characters that CS2 replaces with underscores
        hostname = System.Text.RegularExpressions.Regex.Replace(hostname, @"[^\w\-]", "_");
        Context.DemoName = $"auto-{timestamp}-{map}-{hostname}.dem";

        _ = _db.LogMatchEventAsync(matchId, "match_live", $"{{\"map\":\"{map}\"}}");
        _ = _db.UpdateMatchAsync(matchId, 0, 0, "live", map, mapIdx);
    }

    private static string SideName(TeamSide side) => side switch
    {
        TeamSide.CounterTerrorist => "CT",
        TeamSide.Terrorist        => "T",
        _                         => "Unknown"
    };

    // -------------------------------------------------------------------------
    // Round end
    // -------------------------------------------------------------------------

    public void OnRoundEnd(EventRoundEnd @event)
    {
        if (Context == null) return;

        if (Context.State == MatchState.Knife)
        {
            // Insert knife round as round 0 before handing off
            Console.WriteLine($"[CS2Match] Knife OnRoundEnd: MatchId={Context.Config.MatchId} LobbyId={Context.Config.LobbyId}");
            if (ulong.TryParse(Context.Config.MatchId, out ulong knifelobbyId))
            {
                Console.WriteLine($"[CS2Match] Inserting knife round 0 for lobby {knifelobbyId}");
                string knifeWinner = (@event.Winner == (int)TeamSide.CounterTerrorist)
                    ? (Context.Team1Side == TeamSide.CounterTerrorist ? "team1" : "team2")
                    : (Context.Team1Side == TeamSide.Terrorist ? "team1" : "team2");

                _ = _db.InsertMatchRoundAsync(new MatchRoundRow(
                    knifelobbyId,
                    0,
                    knifeWinner,
                    @event.Reason,
                    Context.Config.Maplist[Context.CurrentMapIndex],
                    0,
                    0,
                    "knife",
                    Context.Team1Side == TeamSide.CounterTerrorist
                ));
            }

            _knifeManager.HandleRoundEnd(@event);
            return;
        }

        if (Context.State != MatchState.Live) return;

        // @event.Winner: 2=T, 3=CT
        if (@event.Winner == (int)TeamSide.CounterTerrorist)
        {
            if (Context.Team1Side == TeamSide.CounterTerrorist) Context.Team1Score++;
            else Context.Team2Score++;
        }
        else if (@event.Winner == (int)TeamSide.Terrorist)
        {
            if (Context.Team1Side == TeamSide.Terrorist) Context.Team1Score++;
            else Context.Team2Score++;
        }

        int round = Context.Team1Score + Context.Team2Score;
        _ = _db.LogRoundEventAsync(new RoundEventData(
            Context.Config.MatchId, round, "round_end",
            Context.Team1Score, Context.Team2Score, @event.Winner));
        _ = _db.UpdateMatchAsync(Context.Config.MatchId,
            Context.Team1Score, Context.Team2Score, "live",
            Context.Config.Maplist[Context.CurrentMapIndex], Context.CurrentMapIndex + 1);

        // Accumulate live time for alive players before the round ends
        float now = CounterStrikeSharp.API.Server.CurrentTime;
        foreach (var stats in Context.PlayerStats.Values)
        {
            if (stats.RoundSpawnTime > 0f)
            {
                stats.LiveTimeSeconds += now - stats.RoundSpawnTime;
                stats.RoundSpawnTime = 0f;
            }
        }
        // Reset per-round bomb tracking
        Context.BombPlantTime = 0f;
        Context.DefuseAttempts.Clear();
        Context.ActiveDefuser = 0;

        // Credit entry win if the entry killer's team won this round
        if (Context.EntryKillerThisRound != 0 && Context.PlayerStats.TryGetValue(Context.EntryKillerThisRound, out var entryStats))
        {
            bool entryTeamWon = (Context.EntryKillerConfigTeam == 1 && Context.Team1Score > 0 &&
                                 ((Context.Team1Side == TeamSide.CounterTerrorist && @event.Winner == (int)TeamSide.CounterTerrorist) ||
                                  (Context.Team1Side == TeamSide.Terrorist        && @event.Winner == (int)TeamSide.Terrorist)))
                             || (Context.EntryKillerConfigTeam == 2 && Context.Team2Score > 0 &&
                                 ((Context.Team2Side == TeamSide.CounterTerrorist && @event.Winner == (int)TeamSide.CounterTerrorist) ||
                                  (Context.Team2Side == TeamSide.Terrorist        && @event.Winner == (int)TeamSide.Terrorist)));
            if (entryTeamWon) entryStats.EntryWins++;
        }

        // Credit clutch win if the clutcher's team won this round
        if (Context.ClutchPlayerId != 0 && Context.PlayerStats.TryGetValue(Context.ClutchPlayerId, out var clutchStats))
        {
            int winnerTeamNum = @event.Winner; // 2=T, 3=CT
            bool clutcherWon = (Context.ClutchPlayerConfigTeam == 1 &&
                                ((Context.Team1Side == TeamSide.CounterTerrorist && winnerTeamNum == (int)TeamSide.CounterTerrorist) ||
                                 (Context.Team1Side == TeamSide.Terrorist        && winnerTeamNum == (int)TeamSide.Terrorist)))
                            || (Context.ClutchPlayerConfigTeam == 2 &&
                                ((Context.Team2Side == TeamSide.CounterTerrorist && winnerTeamNum == (int)TeamSide.CounterTerrorist) ||
                                 (Context.Team2Side == TeamSide.Terrorist        && winnerTeamNum == (int)TeamSide.Terrorist)));
            if (clutcherWon)
            {
                if (Context.ClutchSituation == 1) clutchStats.V1Wins++;
                else clutchStats.V2Wins++;
            }
        }

        // Capture cash earned this round before flushing
        CaptureRoundEndEconomy();

        // Categorise this round's multi-kills before flushing (resets RoundKills)
        ProcessRoundMultiKills();

        // Flush cumulative scoreboard and per-round player stats
        FlushScoreboard(round);

        // Halftime: swap sides at round 12 (configurable via mp_maxrounds/2)
        int maxRounds = 24;
        if (Context.Config.Cvars.TryGetValue("mp_maxrounds", out string? mrStr)
            && int.TryParse(mrStr, out int mr))
            maxRounds = mr;

        // Write per-round row to match_rounds and per-player row to match_round_players
        Console.WriteLine($"[CS2Match] Live OnRoundEnd round={round}: MatchId={Context.Config.MatchId} LobbyId={Context.Config.LobbyId}");
        if (ulong.TryParse(Context.Config.MatchId, out ulong lobbyId))
        {
            Console.WriteLine($"[CS2Match] Inserting round {round} data for lobby {lobbyId}");
            FlushRoundPlayers(round, lobbyId);
            string roundWinner = (@event.Winner == (int)TeamSide.CounterTerrorist)
                ? (Context.Team1Side == TeamSide.CounterTerrorist ? "team1" : "team2")
                : (Context.Team1Side == TeamSide.Terrorist ? "team1" : "team2");

            string half = round <= maxRounds / 2 ? "first"
                        : round <= maxRounds     ? "second"
                                                 : "overtime";

            _ = _db.InsertMatchRoundAsync(new MatchRoundRow(
                lobbyId,
                round,
                roundWinner,
                @event.Reason,
                Context.Config.Maplist[Context.CurrentMapIndex],
                Context.Team1Score,
                Context.Team2Score,
                half,
                Context.Team1Side == TeamSide.CounterTerrorist
            ));
        }

        if (round == maxRounds / 2)
        {
            (Context.Team1Side, Context.Team2Side) = (Context.Team2Side, Context.Team1Side);
            BroadcastAll(" \x04[Match]\x01 Halftime! Teams switch sides.");
        }

        CheckMapWin(maxRounds);
    }

    private void CheckMapWin(int maxRounds)
    {
        if (Context == null) return;

        int toWin = maxRounds / 2 + 1;
        bool t1 = Context.Team1Score >= toWin;
        bool t2 = Context.Team2Score >= toWin;

        if (!t1 && !t2) return;

        // Overtime: both teams reached the regulation win threshold — the map
        // is tied going into overtime. Let CS2 run overtime as configured
        // (mp_overtime_enable, mp_overtime_maxrounds). Only declare a winner
        // once one team leads by more than 1 round past the regulation threshold,
        // meaning they actually won an overtime half.
        if (t1 && t2)
        {
            // Tied at regulation end — CS2 will start overtime automatically.
            // Reset to no-win state and let overtime play out.
            return;
        }

        // In overtime, scores exceed toWin. Only trigger map win when the score
        // gap is decisive (i.e. one team is strictly ahead and the other can no
        // longer catch up within the current overtime period). CS2's own
        // mp_match_can_clinch handles this — just trust EventCsWinPanelMatch.
        // Guard: skip if we are past regulation (scores sum exceeds maxRounds).
        if (Context.Team1Score + Context.Team2Score > maxRounds) return;

        // Regulation win detected — do nothing here; OnMapWin() via
        // EventCsWinPanelMatch is the single authority for map-end logic.
        // This guard just prevents the round-end loop from running into overtime.
    }

    /// <summary>
    /// Called from EventCsWinPanelMatch — CS2 has definitively ended the map
    /// (covers regulation AND overtime). Credit the map win and advance the series.
    /// </summary>
    public void OnMapWin()
    {
        if (Context?.State != MatchState.Live) return;

        bool t1 = Context.Team1Score > Context.Team2Score;
        bool t2 = Context.Team2Score > Context.Team1Score;

        if (t1) Context.MapWinsTeam1++;
        else if (t2) Context.MapWinsTeam2++;
        // exact tie (draw) — no map win credited

        string winner = t1 ? Context.Config.Team1.Name
                      : t2 ? Context.Config.Team2.Name
                           : "Draw";
        BroadcastAll($" \x04[Match]\x01 {winner} wins the map! Series: {Context.MapWinsTeam1}-{Context.MapWinsTeam2}");

        _ = _db.LogMatchEventAsync(Context.Config.MatchId, "map_end",
            $"{{\"winner\":\"{winner}\",\"t1\":{Context.Team1Score},\"t2\":{Context.Team2Score}}}");

        CheckSeriesProgress();
    }

    private void CheckSeriesProgress()
    {
        if (Context == null) return;

        int mapsToWin = Context.Config.ClinchSeries
            ? (int)Math.Ceiling(Context.Config.NumMaps / 2.0)
            : Context.Config.NumMaps;

        bool seriesOver = Context.MapWinsTeam1 >= mapsToWin
                       || Context.MapWinsTeam2 >= mapsToWin
                       || (Context.MapWinsTeam1 + Context.MapWinsTeam2) >= Context.Config.NumMaps;

        if (seriesOver)
        {
            EndMatch();
            return;
        }

        Context.CurrentMapIndex++;
        if (Context.CurrentMapIndex < Context.Config.Maplist.Count)
        {
            BroadcastAll($" \x04[Match]\x01 Loading next map: \x0B{Context.Config.Maplist[Context.CurrentMapIndex]}\x01");
            _mapChanger.ChangeMap(Context.Config.Maplist[Context.CurrentMapIndex]);
        }
        else
        {
            EndMatch();
        }
    }

    private void EndMatch()
    {
        if (Context == null) return;

        string result = Context.MapWinsTeam1 > Context.MapWinsTeam2
            ? $"{Context.Config.Team1.Name} wins the series {Context.MapWinsTeam1}-{Context.MapWinsTeam2}!"
            : Context.MapWinsTeam2 > Context.MapWinsTeam1
                ? $"{Context.Config.Team2.Name} wins the series {Context.MapWinsTeam2}-{Context.MapWinsTeam1}!"
                : $"Series drawn {Context.MapWinsTeam1}-{Context.MapWinsTeam2}!";

        BroadcastAll($" \x04[Match]\x01 {result}");

        string closingMatchId = Context.Config.MatchId;
        int finalRound = Context.Team1Score + Context.Team2Score;

        // Final scoreboard flush then close
        FlushScoreboard(finalRound);
        _ = _db.LogMatchEventAsync(closingMatchId, "series_end",
            $"{{\"t1_maps\":{Context.MapWinsTeam1},\"t2_maps\":{Context.MapWinsTeam2}}}");
        _ = _db.FinishMatchAsync(closingMatchId, Context.Team1Score, Context.Team2Score);
        _ = _db.CloseScoreboardAsync(closingMatchId);

        Console.WriteLine($"[CS2Match] EndMatch: MatchId={Context.Config.MatchId} LobbyId={Context.Config.LobbyId}");
        if (ulong.TryParse(Context.Config.MatchId, out ulong lobbyId))
        {
            string demoName = Context.DemoName;
            Console.WriteLine($"[CS2Match] Finishing lobby {lobbyId} with demo {demoName}");
            _ = _db.FinishLobbyAsync(lobbyId, demoName);
        }
        else
        {
            Console.WriteLine($"[CS2Match] WARNING: Could not parse MatchId '{Context.Config.MatchId}' as ulong — lobby not updated");
        }

        Context = null;

        // Return to AIM map after giving players a moment to read the result
        _plugin.AddTimer(10f, () => _aimManager.EnterAimMode());
    }

    // -------------------------------------------------------------------------
    // Pause / unpause
    // -------------------------------------------------------------------------

    public void OnPauseRequest(CCSPlayerController player)
    {
        if (Context == null) return;

        if (Context.State == MatchState.Paused)
        {
            player.PrintToChat(" \x02[Match]\x01 Already paused. Type \x09.unpause\x01 to vote for resume.");
            return;
        }

        if (Context.State != MatchState.Live) return;

        if (!_pauseManager.IsRegisteredPlayer(player.SteamID))
        {
            player.PrintToChat(" \x02[Match]\x01 You are not registered in this match.");
            return;
        }

        Context.StateBeforePause = Context.State;
        Context.State = MatchState.Paused;
        _pauseManager.Reset();

        Server.ExecuteCommand("mp_pause_match");
        BroadcastAll($" \x04[Match]\x01 \x07Technical pause\x01 called by {player.PlayerName}.");
        BroadcastAll($" \x04[Match]\x01 Both teams must type \x09.unpause\x01 to resume.");

        _ = _db.LogMatchEventAsync(Context.Config.MatchId, "pause",
            $"{{\"by\":\"{player.SteamID}\",\"name\":\"{player.PlayerName}\"}}");
        _ = _db.UpdateMatchAsync(Context.Config.MatchId,
            Context.Team1Score, Context.Team2Score, "paused",
            Context.Config.Maplist[Context.CurrentMapIndex], Context.CurrentMapIndex + 1);
    }

    public void OnUnpauseVote(CCSPlayerController player)
    {
        if (Context?.State != MatchState.Paused) return;

        if (!_pauseManager.IsRegisteredPlayer(player.SteamID))
        {
            player.PrintToChat(" \x02[Match]\x01 You are not registered in this match.");
            return;
        }

        _pauseManager.RegisterUnpause(player.SteamID);
        var (t1, t2) = _pauseManager.GetUnpauseStatus();
        BroadcastAll($" \x04[Match]\x01 Unpause: {Context.Config.Team1.Name} {(t1 ? "✓" : "✗")} | {Context.Config.Team2.Name} {(t2 ? "✓" : "✗")}");
    }

    private void HandleUnpause()
    {
        if (Context?.State != MatchState.Paused) return;
        Context.State = Context.StateBeforePause;
        Server.ExecuteCommand("mp_unpause_match");
        BroadcastAll(" \x04[Match]\x01 Match \x04resumed\x01!");
        _ = _db.LogMatchEventAsync(Context.Config.MatchId, "unpause");
        _ = _db.UpdateMatchAsync(Context.Config.MatchId,
            Context.Team1Score, Context.Team2Score, "live",
            Context.Config.Maplist[Context.CurrentMapIndex], Context.CurrentMapIndex + 1);
    }

    // -------------------------------------------------------------------------
    // Abort
    // -------------------------------------------------------------------------

    public void AbortMatch(bool silent = false)
    {
        if (Context == null) return;
        if (!silent)
        {
            BroadcastAll(" \x02[Match]\x01 Match aborted.");
            _ = _db.LogMatchEventAsync(Context.Config.MatchId, "match_aborted");
        }
        Context = null;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Writes every tracked player's current stats to match_scoreboard.
    /// Safe to call frequently — uses INSERT ... ON DUPLICATE KEY UPDATE.
    /// </summary>
    private void FlushScoreboard(int round)
    {
        if (Context == null) return;
        foreach (var (steamId, stats) in Context.PlayerStats)
        {
            _ = _db.UpsertScoreboardAsync(new ScoreboardRow(
                Context.Config.MatchId,
                steamId,
                stats.PlayerName,
                stats.ConfigTeam,
                stats.TeamName,
                // Core
                stats.Kills,
                stats.Deaths,
                stats.DamageDealt,
                stats.DamageTaken,
                stats.Assists,
                // Multi-kills
                stats.Kills5k,
                stats.Kills4k,
                stats.Kills3k,
                stats.Kills2k,
                // Utility
                stats.GrenadesThrown,
                stats.UtilDamage,
                stats.UtilSuccesses,
                stats.UtilEnemiesHit,
                // Flash
                stats.FlashCount,
                stats.FlashSuccesses,
                // Shots
                stats.ShotsFired,
                stats.ShotsOnTarget,
                // Clutch
                stats.V1Count, stats.V1Wins,
                stats.V2Count, stats.V2Wins,
                // Entry
                stats.EntryCount, stats.EntryWins,
                // Economy
                stats.EquipmentValue,
                stats.MoneyRemaining,   // money_saved = money left after buying
                stats.KillReward,
                (int)stats.LiveTimeSeconds,
                // Kill quality
                stats.Headshots,
                stats.CashEarned,
                stats.EnemiesFlashed,
                // Bomb
                stats.BombPlants,
                stats.BombDefuses,
                // Tracking
                stats.RoundsPlayed,
                round,
                stats.Mvps
            ));
        }
    }

    private void ProcessRoundMultiKills()
    {
        if (Context == null) return;
        foreach (var stats in Context.PlayerStats.Values)
        {
            switch (stats.RoundKills)
            {
                case >= 5: stats.Kills5k++; break;
                case 4:    stats.Kills4k++; break;
                case 3:    stats.Kills3k++; break;
                case 2:    stats.Kills2k++; break;
            }
            // Do NOT reset RoundKills here — FlushRoundPlayers reads it and resets it
        }
    }

    private void FlushRoundPlayers(int round, ulong lobbyId)
    {
        if (Context == null) return;
        var rows = new List<RoundPlayerRow>();
        foreach (var (steamId, stats) in Context.PlayerStats)
        {
            string team = stats.ConfigTeam == 1 ? "team1" : "team2";
            rows.Add(new RoundPlayerRow(
                lobbyId,
                round,
                steamId.ToString(),
                stats.PlayerName,
                team,
                stats.RoundKills,
                stats.RoundDeaths,
                stats.RoundDamageDealt,
                stats.RoundHeadshots,
                stats.RoundAssists,
                stats.RoundEquipmentValue,
                stats.RoundMoneySpent,
                stats.RoundCashEarned
            ));
            // Reset per-round counters
            stats.RoundKills          = 0;
            stats.RoundDeaths         = 0;
            stats.RoundDamageDealt    = 0;
            stats.RoundHeadshots      = 0;
            stats.RoundAssists        = 0;
            stats.RoundEquipmentValue = 0;
            stats.RoundStartAccount   = 0;
            stats.RoundMoneySpent     = 0;
            stats.RoundCashEarned     = 0;
            stats.RoundGotEntry       = false;
        }
        if (rows.Count > 0)
            _ = _db.InsertRoundPlayersAsync(rows);
    }

    private PlayerStats GetOrCreateStats(ulong steamId, string playerName, int configTeam, string teamName)
    {
        if (!Context!.PlayerStats.TryGetValue(steamId, out var stats))
        {
            stats = new PlayerStats
            {
                PlayerName = playerName,
                ConfigTeam = configTeam,
                TeamName   = teamName,
            };
            Context.PlayerStats[steamId] = stats;
        }
        // Always refresh name in case player changed it
        stats.PlayerName = playerName;
        return stats;
    }

    public void RecordKill(ulong attackerSteamId, string attackerName, int attackerConfigTeam, string attackerTeamName,
                           ulong victimSteamId,   string victimName,   int victimConfigTeam,   string victimTeamName,
                           bool headshot, int round)
    {
        if (Context?.State != MatchState.Live) return;

        // Remove victim from alive tracking before clutch check
        if (victimConfigTeam == 1) Context.AliveTeam1.Remove(victimSteamId);
        else if (victimConfigTeam == 2) Context.AliveTeam2.Remove(victimSteamId);

        if (attackerSteamId != 0 && attackerSteamId != victimSteamId)
        {
            var aStats = GetOrCreateStats(attackerSteamId, attackerName, attackerConfigTeam, attackerTeamName);
            aStats.Kills++;
            aStats.RoundKills++;
            if (headshot) { aStats.Headshots++; aStats.RoundHeadshots++; }
            aStats.RoundsPlayed = round;

            // Entry frag: first kill of the round
            if (Context.EntryKillerThisRound == 0)
            {
                Context.EntryKillerThisRound  = attackerSteamId;
                Context.EntryKillerConfigTeam = attackerConfigTeam;
                aStats.EntryCount++;
                aStats.RoundGotEntry = true;
            }
        }

        var vStats = GetOrCreateStats(victimSteamId, victimName, victimConfigTeam, victimTeamName);
        vStats.Deaths++;
        vStats.RoundDeaths++;
        vStats.RoundsPlayed = round;

        // Clutch detection: check if one side is now alone vs multiple enemies
        CheckClutch();
    }

    private void CheckClutch()
    {
        if (Context == null) return;
        int alive1 = Context.AliveTeam1.Count;
        int alive2 = Context.AliveTeam2.Count;

        // 1vN situation: one side has exactly 1 player, other has 1 or 2
        if (alive1 == 1 && alive2 is 1 or 2 && Context.ClutchPlayerId == 0)
        {
            ulong clutcher = Context.AliveTeam1.First();
            Context.ClutchPlayerId         = clutcher;
            Context.ClutchSituation        = alive2;
            Context.ClutchPlayerConfigTeam = 1;
            if (Context.PlayerStats.TryGetValue(clutcher, out var cs))
            {
                if (alive2 == 1) cs.V1Count++;
                else cs.V2Count++;
            }
        }
        else if (alive2 == 1 && alive1 is 1 or 2 && Context.ClutchPlayerId == 0)
        {
            ulong clutcher = Context.AliveTeam2.First();
            Context.ClutchPlayerId         = clutcher;
            Context.ClutchSituation        = alive1;
            Context.ClutchPlayerConfigTeam = 2;
            if (Context.PlayerStats.TryGetValue(clutcher, out var cs))
            {
                if (alive1 == 1) cs.V1Count++;
                else cs.V2Count++;
            }
        }
    }

    public void RecordAssist(ulong assistSteamId, string assistName, int configTeam, string teamName,
                             bool flashAssist, int round)
    {
        if (Context?.State != MatchState.Live) return;
        var stats = GetOrCreateStats(assistSteamId, assistName, configTeam, teamName);
        stats.Assists++;
        stats.RoundAssists++;
        if (flashAssist) stats.FlashAssists++;
        stats.RoundsPlayed = round;
    }

    /// <summary>
    /// Called from EventPlayerHurt — tracks all damage dealt/taken (enemy only for ADR).
    /// Also used for Faceit-style FF detection (caller checks AreSameConfigTeam).
    /// </summary>
    public void RecordDamage(
        ulong attackerSteamId, string attackerName, int attackerConfigTeam, string attackerTeamName,
        ulong victimSteamId,   string victimName,   int victimConfigTeam,   string victimTeamName,
        string weapon, int dmgHealth, int dmgArmor, int round)
    {
        if (Context?.State != MatchState.Live) return;

        // Compute actual health lost using per-player HP tracking.
        // dmgHealth is raw weapon damage and can exceed remaining HP (overkill).
        // We track HP ourselves because player_hurt fires post-damage and
        // event.Health is clamped to 0 on kill shots, making pre-HP unrecoverable
        // from the event alone.
        // HP is updated for ALL damage (FF and enemy) so that a FF hit followed
        // by an enemy hit still produces the correct actual-damage for the enemy.
        int actualDmgHealth;
        if (Context.PlayerCurrentHp.TryGetValue(victimSteamId, out int trackedHp))
        {
            actualDmgHealth = Math.Min(dmgHealth, trackedHp);
            Context.PlayerCurrentHp[victimSteamId] = Math.Max(0, trackedHp - dmgHealth);
        }
        else
        {
            // Fallback: player spawned before tracking was seeded (e.g. hot-reload).
            actualDmgHealth = Math.Min(dmgHealth, 100);
        }

        bool enemyDamage = attackerConfigTeam != 0 && victimConfigTeam != 0
                        && attackerConfigTeam != victimConfigTeam;

        // Friendly fire: HP tracking updated above, but stats are not recorded.
        if (!enemyDamage) return;

        bool isHe   = weapon.Contains("hegrenade",    StringComparison.OrdinalIgnoreCase);
        bool isUtil = weapon.Contains("molotov",       StringComparison.OrdinalIgnoreCase) ||
                      weapon.Contains("incgrenade",    StringComparison.OrdinalIgnoreCase) ||
                      weapon.Contains("inferno",       StringComparison.OrdinalIgnoreCase);

        if (attackerSteamId != 0 && attackerSteamId != victimSteamId)
        {
            var aStats = GetOrCreateStats(attackerSteamId, attackerName, attackerConfigTeam, attackerTeamName);
            aStats.DamageDealt      += actualDmgHealth;
            aStats.RoundDamageDealt += actualDmgHealth;
            aStats.ArmorDamage      += dmgArmor;
            if (isHe) aStats.HeDamageDealt += actualDmgHealth;
            if (isUtil)
            {
                aStats.UtilDamage += actualDmgHealth;
                aStats.UtilEnemiesHit++;
                // Count a util success only once per throw per round
                if (Context.RoundUtilSucceeded.Add(attackerSteamId))
                    aStats.UtilSuccesses++;
            }
            // Each player_hurt from a bullet = one shot that connected
            bool isBullet = !isHe && !isUtil && !weapon.Contains("knife", StringComparison.OrdinalIgnoreCase);
            if (isBullet) aStats.ShotsOnTarget++;
            aStats.RoundsPlayed = round;
        }

        var vStats = GetOrCreateStats(victimSteamId, victimName, victimConfigTeam, victimTeamName);
        vStats.DamageTaken += actualDmgHealth;
        if (isHe) vStats.HeDamageTaken += actualDmgHealth;
        vStats.RoundsPlayed = round;
    }

    /// <summary>
    /// Called from EventPlayerBlind — only counts flashing an enemy.
    /// </summary>
    public void RecordFlash(
        ulong flasherSteamId, string flasherName, int flasherConfigTeam, string flasherTeamName,
        int victimConfigTeam, float duration, int round)
    {
        if (Context?.State != MatchState.Live) return;
        // Only enemies count
        if (flasherConfigTeam == 0 || victimConfigTeam == 0 || flasherConfigTeam == victimConfigTeam) return;

        var stats = GetOrCreateStats(flasherSteamId, flasherName, flasherConfigTeam, flasherTeamName);
        stats.EnemiesFlashed++;
        stats.TotalFlashDuration += duration;
        // FlashSuccesses = number of throws that blinded ≥1 enemy (deduplicated per round)
        if (Context.RoundFlashSucceeded.Add(flasherSteamId))
            stats.FlashSuccesses++;
        stats.RoundsPlayed = round;
    }

    public void RecordGrenade(ulong steamId, string playerName, int configTeam, string teamName, int round, string weapon)
    {
        if (Context?.State != MatchState.Live) return;
        var stats = GetOrCreateStats(steamId, playerName, configTeam, teamName);
        bool isFlash = weapon.Contains("flashbang", StringComparison.OrdinalIgnoreCase);
        if (isFlash)
            stats.FlashCount++;
        else
            stats.GrenadesThrown++;  // utility_count = non-flash grenades (HE, molotov, smoke, etc.)
        stats.RoundsPlayed = round;
    }

    public void RecordMvp(ulong steamId, string playerName, int configTeam, string teamName)
    {
        if (Context?.State != MatchState.Live) return;
        var stats = GetOrCreateStats(steamId, playerName, configTeam, teamName);
        stats.Mvps++;
    }

    public void RecordBombPlant(ulong steamId, string playerName, int configTeam, string teamName, int round)
    {
        if (Context?.State != MatchState.Live) return;
        var stats = GetOrCreateStats(steamId, playerName, configTeam, teamName);
        stats.BombPlants++;
        stats.RoundsPlayed = round;
        // Track plant time so GetBombTimeLeft() can compute remaining time
        Context.BombPlantTime = CounterStrikeSharp.API.Server.CurrentTime;
        // Read mp_c4timer cvar if available
        if (Context.Config.Cvars.TryGetValue("mp_c4timer", out string? timerStr)
            && float.TryParse(timerStr, out float t))
            Context.BombTimerLength = t;
        else
            Context.BombTimerLength = 40f;
        // Reset defuse attempts for this round
        Context.DefuseAttempts.Clear();
        Context.ActiveDefuser = 0;
    }

    public void RecordBombDefuse(ulong steamId, string playerName, int configTeam, string teamName, int round)
    {
        if (Context?.State != MatchState.Live) return;
        var stats = GetOrCreateStats(steamId, playerName, configTeam, teamName);
        stats.BombDefuses++;
        stats.RoundsPlayed = round;
        Context.ActiveDefuser = 0;
    }

    public void RecordDefuseAttempt(ulong steamId)
    {
        if (Context?.State != MatchState.Live) return;
        Context.ActiveDefuser = steamId;
        if (!Context.DefuseAttempts.ContainsKey(steamId))
            Context.DefuseAttempts[steamId] = 0;
        Context.DefuseAttempts[steamId]++;
        if (Context.PlayerStats.TryGetValue(steamId, out var stats))
            stats.DefuseAttempts++;
    }

    public int GetDefuseAttempts(ulong steamId)
    {
        if (Context == null) return 0;
        return Context.DefuseAttempts.TryGetValue(steamId, out int n) ? n : 0;
    }

    public int GetTotalDefuseAttempts()
    {
        if (Context == null) return 0;
        int total = 0;
        foreach (var v in Context.DefuseAttempts.Values) total += v;
        return total;
    }

    public float GetBombTimeLeft()
    {
        if (Context == null || Context.BombPlantTime <= 0f) return 0f;
        float elapsed = CounterStrikeSharp.API.Server.CurrentTime - Context.BombPlantTime;
        float remaining = Context.BombTimerLength - elapsed;
        return remaining < 0f ? 0f : remaining;
    }

    public void RecordShotFired(ulong steamId, string playerName, int configTeam, string teamName, int round)
    {
        if (Context?.State != MatchState.Live) return;
        var stats = GetOrCreateStats(steamId, playerName, configTeam, teamName);
        stats.ShotsFired++;
        stats.RoundsPlayed = round;
    }

    public void RecordShotOnTarget(ulong steamId)
    {
        if (Context?.State != MatchState.Live) return;
        if (Context.PlayerStats.TryGetValue(steamId, out var stats))
            stats.ShotsOnTarget++;
    }

    public void RecordPlayerSpawn(ulong steamId, string playerName, int configTeam, string teamName, int round)
    {
        if (Context?.State != MatchState.Live) return;
        var stats = GetOrCreateStats(steamId, playerName, configTeam, teamName);
        stats.RoundSpawnTime = CounterStrikeSharp.API.Server.CurrentTime;
        stats.RoundsPlayed = round;

        // Track alive sets for clutch detection
        if (configTeam == 1) Context.AliveTeam1.Add(steamId);
        else if (configTeam == 2) Context.AliveTeam2.Add(steamId);

        // Seed HP tracking — every player starts each round at 100 HP
        Context.PlayerCurrentHp[steamId] = 100;
    }

    /// <summary>
    /// Captures each player's account balance at round start (before buying).
    /// Call from OnRoundStart when state is Live.
    /// </summary>
    public void CaptureRoundStartMoney()
    {
        if (Context?.State != MatchState.Live) return;
        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid || player.IsBot) continue;
            int account = player.InGameMoneyServices?.Account ?? 0;
            int configTeam = GetConfigTeamForPlayer(player.SteamID);
            string teamName = GetTeamNameForConfigTeam(configTeam);
            var stats = GetOrCreateStats(player.SteamID, player.PlayerName, configTeam, teamName);
            stats.RoundStartAccount = account;
        }
    }

    /// <summary>
    /// Captures equipment values and money at freeze end (after buying, before round starts).
    /// MoneySpent = money_at_round_start − money_now.
    /// </summary>
    public void CaptureEquipmentValues()
    {
        if (Context?.State != MatchState.Live) return;
        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid || player.IsBot) continue;
            var pawn = player.PlayerPawn?.Value;
            if (pawn == null) continue;
            int configTeam = GetConfigTeamForPlayer(player.SteamID);
            string teamName = GetTeamNameForConfigTeam(configTeam);
            var stats = GetOrCreateStats(player.SteamID, player.PlayerName, configTeam, teamName);
            stats.RoundEquipmentValue = pawn.CurrentEquipmentValue;
            stats.EquipmentValue      = pawn.CurrentEquipmentValue;

            int accountNow = player.InGameMoneyServices?.Account ?? 0;
            stats.MoneyRemaining   = accountNow;
            stats.RoundMoneySpent  = Math.Max(0, stats.RoundStartAccount - accountNow);
            stats.MoneySpent      += stats.RoundMoneySpent;
        }
    }

    /// <summary>
    /// Captures cash earned this round = account delta since freeze end (kills + round bonus).
    /// Call at round end BEFORE FlushRoundPlayers.
    /// </summary>
    public void CaptureRoundEndEconomy()
    {
        if (Context?.State != MatchState.Live) return;
        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid || player.IsBot) continue;
            int account = player.InGameMoneyServices?.Account ?? 0;
            if (!Context.PlayerStats.TryGetValue(player.SteamID, out var stats)) continue;
            // Cash earned this round = money gained after buying phase ended
            int earned = Math.Max(0, account - stats.MoneyRemaining);
            stats.RoundCashEarned = earned;
            stats.CashEarned     += earned;
        }
    }

    /// <summary>
    /// Resets per-round context state. Call at the start of each live round.
    /// </summary>
    public void ResetRoundContext()
    {
        if (Context == null) return;
        Context.EntryKillerThisRound   = 0;
        Context.EntryKillerConfigTeam  = 0;
        Context.ClutchPlayerId         = 0;
        Context.ClutchSituation        = 0;
        Context.ClutchPlayerConfigTeam = 0;
        Context.AliveTeam1.Clear();
        Context.AliveTeam2.Clear();
        Context.RoundUtilSucceeded.Clear();
        Context.RoundFlashSucceeded.Clear();
        Context.PlayerCurrentHp.Clear();
    }

    public bool AreSameConfigTeam(ulong steamId1, ulong steamId2)
    {
        int t1 = GetConfigTeamForPlayer(steamId1);
        int t2 = GetConfigTeamForPlayer(steamId2);
        return t1 != 0 && t1 == t2;
    }

    public int GetConfigTeamForPlayer(ulong steamId)
    {
        if (Context == null) return 0;
        string sid = steamId.ToString();
        if (Context.Config.Team1.Players.ContainsKey(sid)) return 1;
        if (Context.Config.Team2.Players.ContainsKey(sid)) return 2;
        return 0;
    }

    public string GetTeamNameForConfigTeam(int configTeam)
    {
        if (Context == null) return "";
        return configTeam == 1 ? Context.Config.Team1.Name : Context.Config.Team2.Name;
    }

    public (int ready, int required) GetReadyStatus() => _readyManager.GetStatus();

    public int GetCurrentRound() => Context == null ? 0 : Context.Team1Score + Context.Team2Score + 1;

    private static void BroadcastAll(string message)
    {
        foreach (var p in Utilities.GetPlayers())
            if (p.IsValid && !p.IsBot)
                p.PrintToChat(message);
    }
}
