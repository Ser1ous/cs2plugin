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
    private readonly TeamEnforcementManager _enforcement;
    private readonly DatabaseService _db;
    private readonly PluginConfig _pluginConfig;
    private readonly BasePlugin _plugin;
    private readonly GameModeSwitcher _gameModeSwitcher;
    private readonly WebhookNotifier _webhookNotifier;

    public MatchContext? Context { get; private set; }

    /// <summary>
    /// Centralized team-locking. Always non-null; <see cref="TeamEnforcementManager.Enabled"/>
    /// is false in AIM mode and true while a match is loaded.
    /// </summary>
    public TeamEnforcementManager Enforcement => _enforcement;
    public AimManager AimManager => _aimManager;

    public MatchManager(
        ConfigDownloader downloader,
        MapChanger mapChanger,
        CfgExecutor cfgExecutor,
        ReadyManager readyManager,
        PauseManager pauseManager,
        KnifeManager knifeManager,
        AimManager aimManager,
        TeamEnforcementManager enforcement,
        DatabaseService db,
        PluginConfig pluginConfig,
        BasePlugin plugin,
        GameModeSwitcher gameModeSwitcher,
        WebhookNotifier webhookNotifier)
    {
        _downloader      = downloader;
        _mapChanger      = mapChanger;
        _cfgExecutor     = cfgExecutor;
        _readyManager    = readyManager;
        _pauseManager    = pauseManager;
        _knifeManager    = knifeManager;
        _aimManager      = aimManager;
        _enforcement     = enforcement;
        _db              = db;
        _pluginConfig    = pluginConfig;
        _plugin          = plugin;
        _gameModeSwitcher = gameModeSwitcher;
        _webhookNotifier = webhookNotifier;

        _readyManager.OnAllReady         += StartKnifeRound;
        _pauseManager.OnBothTeamsUnpaused += HandleUnpause;
        _knifeManager.OnKnifeRoundWinner  += HandleKnifeWinner;
    }

    public MatchState State => Context?.State ?? MatchState.None;

    // -------------------------------------------------------------------------
    // Load match from URL
    // -------------------------------------------------------------------------

    /// <summary>
    /// Loads a match from URL, forcefully overriding any in-progress match
    /// or AIM session. Design notes:
    ///
    /// • NEVER rejects the load based on current server state. If a match
    ///   is already active (warmup, knife, live, paused) it is torn down
    ///   and the new config takes over. This is intentional — the plugin
    ///   is driven by an external lobby orchestrator and must be a slave
    ///   to whatever URL it's handed.
    /// • Clears AIM-mode cvar overrides (freezetime etc.) before the new
    ///   warmup/competitive cfg runs, so AIM settings never leak into a
    ///   real match.
    /// • The download runs on the thread-pool; the actual state transition
    ///   is marshalled to the main game thread via Server.NextFrame so we
    ///   can safely touch CCSPlayerController, cvars, and change the map.
    /// </summary>
    public async Task LoadMatchFromUrlAsync(string url, Action<string> feedback)
    {
        // NOTE: this runs on the caller's thread up to the first await,
        // so feedback("Downloading...") is still safely on the main
        // game thread. After `await _downloader.DownloadAsync(url)` the
        // continuation resumes on a thread-pool thread (CSS does not
        // install a SynchronizationContext). From that point on NOTHING
        // that touches native CSS state — including the feedback Action
        // (which wraps CommandInfo.ReplyToCommand), ConVar.Find,
        // Server.ExecuteCommand, Context mutation — may be called
        // directly. We marshal everything back to main thread via
        // Server.NextFrame, including the error-path feedback().
        feedback("Downloading match config...");

        MatchConfig? config = null;
        string? downloadError = null;
        try
        {
            config = await _downloader.DownloadAsync(url).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            downloadError = ex.Message;
        }

        Server.NextFrame(() =>
        {
            // Error path: now we're back on the main thread so feedback
            // (→ native ReplyToCommand) is safe to invoke. Before the
            // fix, this line ran on the thread pool and triggered
            // "Native was invoked on a non-main thread".
            if (downloadError != null)
            {
                feedback($"Error loading config: {downloadError}");
                Console.WriteLine($"[CS2Match] LoadMatchFromUrl: download failed — {downloadError}");
                return;
            }
            if (config == null)
            {
                feedback("Error loading config: empty response");
                return;
            }
            // ---------- Force-override: wipe any prior state ----------
            // This path runs for every load, regardless of whether a match
            // is active, the server is in AIM mode, or it's been idle. We
            // intentionally do NOT return early on "match already running".
            bool wasInMatch = Context != null;
            var priorState  = Context?.State ?? MatchState.None;

            if (wasInMatch)
            {
                Console.WriteLine(
                    $"[CS2Match] LoadMatchFromUrl: FORCE-OVERRIDE active match " +
                    $"(prior state: {priorState}) with new config from {url}");
                feedback($"Overriding active match (was: {priorState}) with new config...");
                BroadcastAll(" \x02[Match]\x01 New match config received — aborting current match.");
            }
            else
            {
                Console.WriteLine($"[CS2Match] LoadMatchFromUrl: loading {url} (server was in AIM / idle)");
            }

            // Tear down any prior match's sub-manager state and clear
            // Context. This runs synchronously before we build the new
            // Context so ready/pause/enforcement dicts never alias.
            AbortMatch(silent: true);

            // Revert any AIM-only cvar overrides (e.g. mp_freezetime 2)
            // so the upcoming competitive preset / warmup.cfg / JSON
            // cvars cleanly overwrite them. Without this, an AIM→Live
            // transition could leave the 2s freezetime in effect until
            // the first cfg load.
            _aimManager.ClearAimOverrides();

            // ---------- Install the new match context ----------
            // Order matters: we create the Context BEFORE scheduling the
            // delayed map change so that OnMapStart (which will fire when
            // the changelevel lands ~1s from now) sees Context != null
            // and routes into the Warmup path instead of the AIM path.
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
            _enforcement.LoadFromConfig(config);

            feedback($"Loaded: {config.MatchId} | {config.Team1.Name} vs {config.Team2.Name} | Map: {config.Maplist[0]}");
            _ = _db.LogMatchEventAsync(config.MatchId, "match_loaded",
                $"{{\"url\":\"{url}\",\"force_override\":{(wasInMatch ? "true" : "false")},\"prior_state\":\"{priorState}\"}}");
            _ = _db.CreateMatchAsync(new MatchRow(
                config.MatchId,
                config.LobbyId,
                config.Team1.Name,
                config.Team2.Name,
                config.Maplist[0],
                config.NumMaps
            ));

            string targetMap  = config.Maplist[0];
            bool alreadyOnMap = string.Equals(Server.MapName, targetMap, StringComparison.OrdinalIgnoreCase);

            if (alreadyOnMap)
            {
                // Fast path: same map. No need for the 1-second delayed
                // changelevel dance — mp_restartgame is enough. OnMapStart
                // does NOT fire in this path so we set PendingWarmup
                // ourselves; the next OnFirstRoundStart picks it up.
                Console.WriteLine(
                    $"[CS2Match] LoadMatchFromUrl: fast-path restart " +
                    $"(same map already loaded)");
                Context.PendingWarmup = true;
                Server.ExecuteCommand("mp_warmup_pausetimer 0");
                Server.ExecuteCommand("mp_unpause_match");
                Server.ExecuteCommand("mp_restartgame 3");
                return;
            }

            // Slow path: different map. Use the delayed, cancellable
            // map-change helper. Rapid repeats of this call (admin spamming
            // load_url) cancel the previous pending callback via the
            // sequence-number check inside MapChangeDelayed, so we never
            // stack map reloads.
            Console.WriteLine(
                $"[CS2Match] LoadMatchFromUrl: scheduling delayed map change " +
                $"(target map={targetMap})");

            _gameModeSwitcher.MapChangeDelayed(
                targetMap,
                label: "Match load",
                delaySeconds: 1.0f);
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
            // AIM mode: flag pending so OnFirstRoundStart applies the aim cfg.
            // Also reset AimManager's "applied" flag AND its loop guard so
            // the new map re-runs the cfg (and importantly re-locks
            // mp_freezetime 2) either on the first RoundStart or on the
            // first player connect — whichever fires first.
            //
            // OnAimMapStart() also re-asserts mp_warmup_pausetimer 0,
            // sv_pausable 0, and mp_unpause_match — guarding against the
            // "stuck pause" symptom where a previous match's pause state
            // survived a map reload into AIM.
            Console.WriteLine($"[CS2Match] Map starting (AIM mode): {mapName}");
            _aimManager.OnAimMapStart();
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
                _aimManager.EnsureAimApplied();
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

        // Belt-and-braces: clear any lingering pause state from a previous
        // match/abort before applying the warmup cfg. warmup.cfg sets
        // mp_warmup_pausetimer 1 which freezes the timer until ready-up,
        // but mp_pause_match could still be held over from the previous
        // context (e.g. aborted during a tech pause).
        Server.ExecuteCommand("mp_unpause_match");

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

        // Bug fix: warmup.cfg sets mp_warmup_pausetimer 1 (timer frozen until
        // everyone is ready). knife.cfg does NOT reset it, so the frozen
        // pause state leaks through and mp_warmup_end cannot exit warmup —
        // the match hangs indefinitely on the knife round transition.
        //
        // Explicitly clear both the warmup pause timer AND any active match
        // pause here before issuing restartgame/warmup_end. Order matters:
        // unpause first, then the restart, then drop warmup.
        Server.ExecuteCommand("mp_warmup_pausetimer 0");
        Server.ExecuteCommand("mp_unpause_match");

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
        // Same leftover-pause fix as StartKnifeRound: warmup.cfg froze the
        // pause timer, so clear it before exiting warmup.
        _cfgExecutor.ExecCfg(_pluginConfig.CompetitiveCfgName);
        _cfgExecutor.ExecCvars(Context.Config.Cvars);
        Server.ExecuteCommand("mp_warmup_pausetimer 0");
        Server.ExecuteCommand("mp_unpause_match");
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
        // Delegated to TeamEnforcementManager so the switches are pre-authorized
        // and don't trip the EventPlayerTeam revert logic.
        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid || player.IsBot) continue;
            _enforcement.EnforceSide(player, Context);
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

        Context.RoundEnded = true;

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

        // Outbound round-end webhook (fire-and-forget, no-op if URL unset).
        _webhookNotifier.PostRoundEnd(
            _pluginConfig.RoundEndWebhookUrl, Context.Config.MatchId, round);
        _ = _db.UpdateMatchAsync(Context.Config.MatchId,
            Context.Team1Score, Context.Team2Score, "live",
            Context.Config.Maplist[Context.CurrentMapIndex], Context.CurrentMapIndex + 1);

        // Clear spawn times (engine LiveTime is authoritative; read in SyncStatsFromEngine)
        foreach (var stats in Context.PlayerStats.Values)
            stats.RoundSpawnTime = 0f;
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
                switch (Context.ClutchSituation)
                {
                    case 1: clutchStats.V1Wins++; break;
                    case 2: clutchStats.V2Wins++; break;
                    case 3: clutchStats.V3Wins++; break;
                    case 4: clutchStats.V4Wins++; break;
                    case 5: clutchStats.V5Wins++; break;
                }
            }
        }

        // Capture cash earned this round before flushing
        CaptureRoundEndEconomy();

        // Categorise this round's multi-kills before flushing (resets RoundKills)
        ProcessRoundMultiKills();

        // Sync authoritative stats from engine MatchStats (MatchZy approach)
        SyncStatsFromEngine(round);

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
        // is going into (or already in) overtime. Single-OT rule: we only
        // allow ONE overtime period; if it ends tied the map is a draw.
        if (t1 && t2)
        {
            int otMaxRounds = GetOvertimeMaxRounds();   // default 6 (3+3)
            int totalPlayed = Context.Team1Score + Context.Team2Score;

            // OT1 has fully played out (regulation rounds + one full OT) and
            // the score is still tied → declare a draw immediately. EventCs-
            // WinPanelMatch won't fire for a tie because mp_overtime_enable
            // would normally start OT2; we preempt that here.
            if (totalPlayed >= maxRounds + otMaxRounds &&
                Context.Team1Score == Context.Team2Score)
            {
                BroadcastAll(" \x04[Match]\x01 Single overtime ended tied — match is a \x09DRAW\x01.");
                // Disable further overtimes so the engine doesn't try to
                // queue OT2 between now and the manual map-end.
                Server.ExecuteCommand("mp_overtime_enable 0");
                // Defer one frame so this OnRoundEnd callback fully unwinds
                // before the map-end / changemap path runs.
                Server.NextFrame(OnMapWin);
                return;
            }

            // Otherwise, let OT1 play out — CS2 starts overtime automatically.
            return;
        }

        // In overtime one side has pulled ahead. Trust EventCsWinPanelMatch
        // for the actual map end; this guard just prevents the round-end
        // loop from declaring a winner before the engine does.
        if (Context.Team1Score + Context.Team2Score > maxRounds) return;
    }

    /// <summary>
    /// Resolves mp_overtime_maxrounds from the loaded config (or live cvar
    /// fallback). Defaults to 6 (3+3) which matches MR12 conventions.
    /// </summary>
    private int GetOvertimeMaxRounds()
    {
        if (Context != null &&
            Context.Config.Cvars.TryGetValue("mp_overtime_maxrounds", out var s) &&
            int.TryParse(s, out var v) && v > 0)
            return v;
        var cv = ConVar.Find("mp_overtime_maxrounds");
        if (cv != null && cv.GetPrimitiveValue<int>() > 0)
            return cv.GetPrimitiveValue<int>();
        return 6;
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
        bool draw = !t1 && !t2;

        if (t1)        Context.MapWinsTeam1++;
        else if (t2)   Context.MapWinsTeam2++;
        else           Context.MapDraws++;   // CS_WM_DRAW — counted as a played map

        string winner = t1 ? Context.Config.Team1.Name
                      : t2 ? Context.Config.Team2.Name
                           : "Draw";

        if (draw)
        {
            BroadcastAll($" \x04[Match]\x01 Map ended in a \x09DRAW\x01 ({Context.Team1Score}-{Context.Team2Score}). Series: {Context.MapWinsTeam1}-{Context.MapWinsTeam2} ({Context.MapDraws} draw{(Context.MapDraws == 1 ? "" : "s")})");
        }
        else
        {
            BroadcastAll($" \x04[Match]\x01 {winner} wins the map! Series: {Context.MapWinsTeam1}-{Context.MapWinsTeam2}");
        }

        _ = _db.LogMatchEventAsync(Context.Config.MatchId, "map_end",
            $"{{\"winner\":\"{winner}\",\"t1\":{Context.Team1Score},\"t2\":{Context.Team2Score},\"draw\":{(draw ? "true" : "false")}}}");

        // Outbound map-end webhook (fire-and-forget, no-op if URL unset).
        _webhookNotifier.PostMapEnd(_pluginConfig.MapEndWebhookUrl, Context.Config.MatchId);

        CheckSeriesProgress();
    }

    private void CheckSeriesProgress()
    {
        if (Context == null) return;

        int mapsToWin = Context.Config.ClinchSeries
            ? (int)Math.Ceiling(Context.Config.NumMaps / 2.0)
            : Context.Config.NumMaps;

        // Draws count toward total maps played but not toward either team's
        // win tally. Without including them in this check, a 1-map match
        // that ended in a draw would advance to a non-existent next map.
        int mapsPlayed = Context.MapWinsTeam1 + Context.MapWinsTeam2 + Context.MapDraws;

        bool seriesOver = Context.MapWinsTeam1 >= mapsToWin
                       || Context.MapWinsTeam2 >= mapsToWin
                       || mapsPlayed >= Context.Config.NumMaps;

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
            $"{{\"t1_maps\":{Context.MapWinsTeam1},\"t2_maps\":{Context.MapWinsTeam2},\"draws\":{Context.MapDraws}}}");
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

        _enforcement.Clear();
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

    /// <summary>
    /// Tears down the current match state AND every sub-manager that holds
    /// match-scoped data (ready votes, pause votes, enforcement dictionary).
    /// Also clears any lingering server pause state so a subsequent
    /// LoadMatchFromUrlAsync can immediately execute cfgs / restartgame
    /// without the engine swallowing commands under a frozen pause.
    ///
    /// Safe to call when Context is already null (acts as a "reset to
    /// neutral server state" helper used by force-override loads from AIM).
    /// </summary>
    public void AbortMatch(bool silent = false)
    {
        bool hadMatch = Context != null;

        if (hadMatch && !silent)
        {
            BroadcastAll(" \x02[Match]\x01 Match aborted.");
            _ = _db.LogMatchEventAsync(Context!.Config.MatchId, "match_aborted");
        }

        // Sub-manager state reset — previously these held stale config
        // references across aborts, which caused ghost !ready votes and
        // leftover unpause votes to carry into the next match.
        _readyManager.Reset();
        _pauseManager.Reset();
        _enforcement.Clear();

        // Drop any active server-side pause so cfg execs and restartgame
        // actually take effect on the next frame. Harmless no-op if the
        // match wasn't paused.
        Server.ExecuteCommand("mp_unpause_match");
        Server.ExecuteCommand("mp_warmup_pausetimer 0");

        Context = null;

        if (hadMatch)
            Console.WriteLine("[CS2Match] AbortMatch: full state reset complete");
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
                // Core (from engine via SyncStatsFromEngine)
                stats.Kills,
                stats.Deaths,
                stats.DamageDealt,
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
                // Engine HP totals
                stats.HealthPointsRemovedTotal,
                stats.HealthPointsDealtTotal,
                // Shots
                stats.ShotsFired,
                stats.ShotsOnTarget,
                // Clutch
                stats.V1Count, stats.V1Wins,
                stats.V2Count, stats.V2Wins,
                stats.V3Count, stats.V3Wins,
                stats.V4Count, stats.V4Wins,
                stats.V5Count, stats.V5Wins,
                // Entry
                stats.EntryCount, stats.EntryWins,
                // Economy
                stats.EquipmentValue,
                stats.MoneySaved,
                stats.KillReward,
                stats.LiveTime,
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

    // -------------------------------------------------------------------------
    // Engine stats sync (MatchZy approach)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Snapshots each player's engine MatchStats at the start of the round
    /// (called from OnRoundFreezeEnd). Deltas at round end give per-round values.
    /// </summary>
    public void SnapshotEngineStats()
    {
        if (Context?.State != MatchState.Live) return;
        Context.EngineSnapshots.Clear();

        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid || player.IsBot) continue;
            if (player.ActionTrackingServices?.MatchStats is not { } ms) continue;
            Context.EngineSnapshots[player.SteamID] = new EngineStatsSnapshot
            {
                Damage        = ms.Damage,
                Kills         = ms.Kills,
                Deaths        = ms.Deaths,
                Assists       = ms.Assists,
                HeadShotKills = ms.HeadShotKills,
            };
        }
    }

    /// <summary>
    /// Reads authoritative cumulative stats from the engine's
    /// ActionTrackingServices.MatchStats for every tracked player,
    /// overwriting the manually-accumulated values in PlayerStats.
    /// This is the core of the MatchZy approach: the engine handles
    /// overkill capping, round boundaries, and death state correctly.
    /// Call at round end BEFORE FlushScoreboard / FlushRoundPlayers.
    /// </summary>
    private void SyncStatsFromEngine(int round)
    {
        if (Context == null) return;

        foreach (var player in Utilities.GetPlayers())
        {
            if (!player.IsValid || player.IsBot) continue;
            if (!Context.PlayerStats.TryGetValue(player.SteamID, out var stats)) continue;
            if (player.ActionTrackingServices?.MatchStats is not { } ms) continue;

            // Snapshot from round start (for per-round deltas)
            var snap = Context.EngineSnapshots.GetValueOrDefault(player.SteamID);

            // --- Per-round deltas (compute BEFORE overwriting cumulative) ---
            stats.RoundDamageDealt = ms.Damage        - snap.Damage;
            stats.RoundDeaths      = ms.Deaths        - snap.Deaths;
            stats.RoundHeadshots   = ms.HeadShotKills - snap.HeadShotKills;
            stats.RoundAssists     = ms.Assists       - snap.Assists;
            // RoundKills is already tracked manually via RecordKill

            // --- Core (cumulative, authoritative) ---
            stats.Kills    = ms.Kills;
            stats.Deaths   = ms.Deaths;
            stats.Assists  = ms.Assists;
            stats.Headshots = ms.HeadShotKills;

            // --- Damage ---
            stats.DamageDealt = ms.Damage;
            // HealthPointsRemovedTotal / HealthPointsDealtTotal use ref-return
            // signatures that vary by server CSS version — left at 0 (DB column exists
            // but will show 0 until a compatible CSS version is confirmed).

            // --- Multi-kills ---
            stats.Kills2k = ms.Enemy2Ks;
            stats.Kills3k = ms.Enemy3Ks;
            stats.Kills4k = ms.Enemy4Ks;
            stats.Kills5k = ms.Enemy5Ks;

            // --- Entry ---
            stats.EntryCount = ms.EntryCount;
            stats.EntryWins  = ms.EntryWins;

            // --- Clutch (1v1, 1v2 from engine; 1v3+ tracked manually) ---
            stats.V1Count = ms.I1v1Count;
            stats.V1Wins  = ms.I1v1Wins;
            stats.V2Count = ms.I1v2Count;
            stats.V2Wins  = ms.I1v2Wins;

            // --- Utility ---
            stats.GrenadesThrown = ms.Utility_Count;
            stats.UtilDamage     = ms.UtilityDamage;
            stats.UtilSuccesses  = ms.Utility_Successes;
            stats.UtilEnemiesHit = ms.Utility_Enemies;

            // --- Flash ---
            stats.FlashCount     = ms.Flash_Count;
            stats.FlashSuccesses = ms.Flash_Successes;
            stats.EnemiesFlashed = ms.EnemiesFlashed;

            // --- Shots ---
            stats.ShotsFired    = ms.ShotsFiredTotal;
            stats.ShotsOnTarget = ms.ShotsOnTargetTotal;

            // --- Economy ---
            stats.EquipmentValue = ms.EquipmentValue;
            stats.MoneySaved     = ms.MoneySaved;
            stats.KillReward     = ms.KillReward;
            stats.CashEarned     = ms.CashEarned;

            // --- Time & MVP ---
            stats.LiveTime = ms.LiveTime;
            stats.Mvps     = player.MVPs;

            // --- Tracking ---
            stats.RoundsPlayed = round;
            stats.LastRound    = round;
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
            // RoundKills is needed for multi-kill categorisation at round end
            aStats.RoundKills++;

            // Entry frag tracking (context-level, for round-end win crediting)
            if (Context.EntryKillerThisRound == 0)
            {
                Context.EntryKillerThisRound  = attackerSteamId;
                Context.EntryKillerConfigTeam = attackerConfigTeam;
                aStats.RoundGotEntry = true;
            }
        }

        // Ensure victim has a stats entry (engine sync fills cumulative Deaths)
        GetOrCreateStats(victimSteamId, victimName, victimConfigTeam, victimTeamName);

        // Clutch detection: check if one side is now alone vs N enemies
        CheckClutch();
    }

    private void CheckClutch()
    {
        if (Context == null) return;
        if (Context.ClutchPlayerId != 0) return; // already tracking a clutch this round

        int alive1 = Context.AliveTeam1.Count;
        int alive2 = Context.AliveTeam2.Count;

        // 1vN situation: one side has exactly 1 player, other has 1–5
        ulong clutcher = 0;
        int opponents = 0;
        int clutcherConfigTeam = 0;

        if (alive1 == 1 && alive2 >= 1 && alive2 <= 5)
        {
            clutcher = Context.AliveTeam1.First();
            opponents = alive2;
            clutcherConfigTeam = 1;
        }
        else if (alive2 == 1 && alive1 >= 1 && alive1 <= 5)
        {
            clutcher = Context.AliveTeam2.First();
            opponents = alive1;
            clutcherConfigTeam = 2;
        }

        if (clutcher == 0) return;

        Context.ClutchPlayerId         = clutcher;
        Context.ClutchSituation        = opponents;
        Context.ClutchPlayerConfigTeam = clutcherConfigTeam;

        if (Context.PlayerStats.TryGetValue(clutcher, out var cs))
        {
            switch (opponents)
            {
                case 1: cs.V1Count++; break;
                case 2: cs.V2Count++; break;
                case 3: cs.V3Count++; break;
                case 4: cs.V4Count++; break;
                case 5: cs.V5Count++; break;
            }
        }
    }

    public void RecordAssist(ulong assistSteamId, string assistName, int configTeam, string teamName,
                             bool flashAssist, int round)
    {
        if (Context?.State != MatchState.Live) return;
        // Engine MatchStats provides cumulative Assists; only FlashAssists is manual.
        if (!flashAssist) return;
        var stats = GetOrCreateStats(assistSteamId, assistName, configTeam, teamName);
        stats.FlashAssists++;
    }

    /// <summary>
    /// Called from EventPlayerHurt — ensures both attacker and victim have stats entries.
    /// All cumulative damage values (DamageDealt, UtilDamage, ShotsOnTarget, etc.) are
    /// read from the engine's ActionTrackingServices.MatchStats at round end by
    /// SyncStatsFromEngine() (MatchZy approach). No manual HP tracking is needed.
    /// </summary>
    public void RecordDamage(
        ulong attackerSteamId, string attackerName, int attackerConfigTeam, string attackerTeamName,
        ulong victimSteamId,   string victimName,   int victimConfigTeam,   string victimTeamName,
        string weapon, int dmgHealth, int dmgArmor, int round)
    {
        if (Context?.State != MatchState.Live) return;

        // Ensure both players have stats entries so SyncStatsFromEngine can find them
        if (attackerSteamId != 0)
            GetOrCreateStats(attackerSteamId, attackerName, attackerConfigTeam, attackerTeamName);
        GetOrCreateStats(victimSteamId, victimName, victimConfigTeam, victimTeamName);
    }

    /// <summary>
    /// Called from EventPlayerBlind — engine MatchStats tracks EnemiesFlashed,
    /// FlashCount, FlashSuccesses cumulatively. This is now a no-op; kept for
    /// API compatibility with EventHandler.
    /// </summary>
    public void RecordFlash(
        ulong flasherSteamId, string flasherName, int flasherConfigTeam, string flasherTeamName,
        int victimConfigTeam, float duration, int round)
    {
        // Engine MatchStats provides all flash stats — SyncStatsFromEngine reads them.
    }

    /// <summary>
    /// Engine MatchStats provides Utility_Count, Flash_Count — no-op.
    /// </summary>
    public void RecordGrenade(ulong steamId, string playerName, int configTeam, string teamName, int round, string weapon)
    {
        // Engine MatchStats provides all grenade stats — SyncStatsFromEngine reads them.
    }

    /// <summary>
    /// Engine provides player.MVPs — no-op; SyncStatsFromEngine reads it.
    /// </summary>
    public void RecordMvp(ulong steamId, string playerName, int configTeam, string teamName)
    {
        // Engine provides MVPs directly — SyncStatsFromEngine reads player.MVPs.
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
        Context.ActiveDefuser     = 0;
        Context.LastDefuserSteamId  = 0;
        Context.LastDefuserName     = "";
        Context.LastDefuserHasKit   = false;
        Context.LastDefuseStartTime = 0f;
    }

    public void RecordBombDefuse(ulong steamId, string playerName, int configTeam, string teamName, int round)
    {
        if (Context?.State != MatchState.Live) return;
        var stats = GetOrCreateStats(steamId, playerName, configTeam, teamName);
        stats.BombDefuses++;
        stats.RoundsPlayed = round;
        Context.ActiveDefuser = 0;
    }

    public void RecordDefuseAttempt(ulong steamId, string playerName, bool hasKit)
    {
        if (Context?.State != MatchState.Live) return;
        Context.ActiveDefuser = steamId;
        if (!Context.DefuseAttempts.ContainsKey(steamId))
            Context.DefuseAttempts[steamId] = 0;
        Context.DefuseAttempts[steamId]++;
        if (Context.PlayerStats.TryGetValue(steamId, out var stats))
            stats.DefuseAttempts++;

        // Snapshot the most recent defuse attempt so OnBombExploded can
        // attribute "needed N more seconds" even if the player aborted.
        Context.LastDefuserSteamId  = steamId;
        Context.LastDefuserName     = playerName;
        Context.LastDefuserHasKit   = hasKit;
        Context.LastDefuseStartTime = CounterStrikeSharp.API.Server.CurrentTime;
    }

    /// <summary>
    /// Last-defuser snapshot used by the bomb_exploded log path. Returns
    /// (steamId=0, ...) if no defuse attempt was made this round.
    /// <paramref name="secondsNeeded"/> is how much more time the defuser
    /// needed to finish at the moment the bomb exploded:
    ///   secondsNeeded = (startTime + duration) - now
    /// where duration is 5s with kit / 10s without. Clamped to 0 if the
    /// defuse should already have completed.
    /// </summary>
    public (ulong steamId, string name, bool hasKit, float secondsNeeded) GetLastDefuserAtExplosion()
    {
        if (Context == null || Context.LastDefuserSteamId == 0)
            return (0, "", false, 0f);

        float duration = Context.LastDefuserHasKit ? 5f : 10f;
        float now      = CounterStrikeSharp.API.Server.CurrentTime;
        float needed   = (Context.LastDefuseStartTime + duration) - now;
        if (needed < 0f) needed = 0f;

        return (Context.LastDefuserSteamId,
                Context.LastDefuserName,
                Context.LastDefuserHasKit,
                needed);
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

    public void RecordPlayerSpawn(ulong steamId, string playerName, int configTeam, string teamName, int round)
    {
        if (Context?.State != MatchState.Live) return;
        var stats = GetOrCreateStats(steamId, playerName, configTeam, teamName);
        stats.RoundSpawnTime = CounterStrikeSharp.API.Server.CurrentTime;

        // Track alive sets for clutch detection
        if (configTeam == 1) Context.AliveTeam1.Add(steamId);
        else if (configTeam == 2) Context.AliveTeam2.Add(steamId);
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

            int accountNow = player.InGameMoneyServices?.Account ?? 0;
            stats.MoneySaved       = accountNow;  // engine MoneySaved = money left after buying
            stats.RoundMoneySpent  = Math.Max(0, stats.RoundStartAccount - accountNow);
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
            int earned = Math.Max(0, account - stats.MoneySaved);
            stats.RoundCashEarned = earned;
        }
    }

    /// <summary>
    /// Resets per-round context state. Call at the start of each live round.
    /// </summary>
    public void ResetRoundContext()
    {
        if (Context == null) return;
        // Pin the round number now, before any score changes can happen this round.
        // GetCurrentRound() reads this field so post-round events (planted_c4 kills,
        // etc.) that fire after OnRoundEnd has already incremented the scores will
        // still return the correct round.
        Context.CurrentRound = Context.Team1Score + Context.Team2Score + 1;
        Context.EntryKillerThisRound   = 0;
        Context.EntryKillerConfigTeam  = 0;
        Context.ClutchPlayerId         = 0;
        Context.ClutchSituation        = 0;
        Context.ClutchPlayerConfigTeam = 0;
        Context.AliveTeam1.Clear();
        Context.AliveTeam2.Clear();
        Context.RoundUtilSucceeded.Clear();
        Context.RoundFlashSucceeded.Clear();
        Context.EngineSnapshots.Clear();
        Context.BombExploded = false;
        Context.RoundEnded   = false;
    }

    /// <summary>
    /// Called by the EventPlayerTeam handler when an engine-driven silent
    /// swap moves a player to the side opposite of what Team1Side/Team2Side
    /// currently tracks. Flips the internal mapping so subsequent reconnects
    /// and respawns land on the correct side.
    ///
    /// Idempotency: this is invoked from inside a swap burst (one event per
    /// player). The first call flips the mapping; from that point on, every
    /// other player in the burst will already match and the handler will
    /// short-circuit before calling this method. The <see cref="_lastSwapTick"/>
    /// guard adds belt-and-braces protection against the edge case where the
    /// flip would otherwise be detected twice in the same server tick.
    /// </summary>
    public void OnEngineSideSwap()
    {
        if (Context == null) return;
        if (Context.State != MatchState.Live && Context.State != MatchState.Paused) return;

        int tick = Server.TickCount;
        if (tick == _lastEngineSwapTick) return;
        _lastEngineSwapTick = tick;

        var prev1 = Context.Team1Side;
        var prev2 = Context.Team2Side;
        Context.Team1Side = prev2;
        Context.Team2Side = prev1;

        Console.WriteLine(
            $"[CS2Match] OnEngineSideSwap: detected engine swap. " +
            $"Team1Side {prev1}→{Context.Team1Side}, Team2Side {prev2}→{Context.Team2Side}");
    }

    private int _lastEngineSwapTick = -1;

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

    public int GetCurrentRound()
    {
        if (Context == null) return 0;
        // Use the pinned value set at round-start. Falls back to the score-based
        // formula only before the very first round (CurrentRound == 0).
        return Context.CurrentRound > 0
            ? Context.CurrentRound
            : Context.Team1Score + Context.Team2Score + 1;
    }

    private static void BroadcastAll(string message)
    {
        foreach (var p in Utilities.GetPlayers())
            if (p.IsValid && !p.IsBot)
                p.PrintToChat(message);
    }
}
