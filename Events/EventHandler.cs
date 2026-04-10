using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CS2MatchPlugin.Commands;
using CS2MatchPlugin.Managers;
using CS2MatchPlugin.Services;
using CS2MatchPlugin.State;

namespace CS2MatchPlugin.Events;

public class PluginEventHandler
{
    private readonly MatchManager _matchManager;
    private readonly PlayerCommands _playerCommands;
    private readonly DatabaseService _db;

    public PluginEventHandler(
        MatchManager matchManager,
        PlayerCommands playerCommands,
        DatabaseService db)
    {
        _matchManager = matchManager;
        _playerCommands = playerCommands;
        _db = db;
    }

    // -------------------------------------------------------------------------
    // Map lifecycle
    // -------------------------------------------------------------------------

    public void OnMapStart(string mapName)
    {
        _matchManager.OnMapStart(mapName);
    }

    // -------------------------------------------------------------------------
    // Round events
    // -------------------------------------------------------------------------

    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        // Deferred warmup/AIM cfg — server is now fully active when RoundStart fires
        _matchManager.OnFirstRoundStart(CounterStrikeSharp.API.Server.MapName);

        var ctx = _matchManager.Context;

        if (ctx?.State == MatchState.Warmup)
        {
            var (ready, required) = _matchManager.GetReadyStatus();
            BroadcastAll($" \x04[Match]\x01 Warmup in progress. Players ready: \x09{ready}/{required}\x01 | Type \x09!ready\x01");
        }
        else if (ctx?.State == MatchState.SidePick)
        {
            // Round 2 of knife is starting its freeze time — show side pick reminder.
            // The match is already paused from HandleKnifeWinner's mp_pause_match.
            _matchManager.BroadcastSidePickInfo();
        }
        else if (ctx?.State == MatchState.Live)
        {
            _matchManager.ResetRoundContext();
            _matchManager.CaptureRoundStartMoney();
        }

        return HookResult.Continue;
    }

    public HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        _matchManager.OnRoundEnd(@event);
        return HookResult.Continue;
    }

    public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        var ctx = _matchManager.Context;
        if (ctx?.State == MatchState.Live)
        {
            int round = _matchManager.GetCurrentRound();
            _ = _db.LogRoundEventAsync(new RoundEventData(
                ctx.Config.MatchId, round, "round_start",
                ctx.Team1Score, ctx.Team2Score, null));

            // Capture equipment values now — this is the only reliable moment
            // (after buying is complete, before movement begins)
            _matchManager.CaptureEquipmentValues();
        }
        return HookResult.Continue;
    }

    // -------------------------------------------------------------------------
    // Kill events
    // -------------------------------------------------------------------------

    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var ctx = _matchManager.Context;
        // Skip kill tracking during knife rounds — only record during Live state
        if (ctx?.State != MatchState.Live) return HookResult.Continue;

        var victim   = @event.Userid;
        var attacker = @event.Attacker;
        var assister = @event.Assister;

        if (victim == null || !victim.IsValid) return HookResult.Continue;

        int round = _matchManager.GetCurrentRound();

        ulong? attackerSteamId = null;
        string? attackerName = null;
        int? attackerTeam = null;
        float? ax = null, ay = null, az = null;

        int vCfgTeam  = _matchManager.GetConfigTeamForPlayer(victim.SteamID);
        string vTeamName = _matchManager.GetTeamNameForConfigTeam(vCfgTeam);

        if (attacker != null && attacker.IsValid && attacker != victim)
        {
            attackerSteamId = attacker.SteamID;
            attackerName    = attacker.PlayerName;
            attackerTeam    = attacker.TeamNum;
            var aPos = attacker.PlayerPawn?.Value?.AbsOrigin;
            if (aPos != null) { ax = aPos.X; ay = aPos.Y; az = aPos.Z; }

            int aCfgTeam  = _matchManager.GetConfigTeamForPlayer(attacker.SteamID);
            string aTeamName = _matchManager.GetTeamNameForConfigTeam(aCfgTeam);

            _matchManager.RecordKill(
                attacker.SteamID, attacker.PlayerName, aCfgTeam, aTeamName,
                victim.SteamID,   victim.PlayerName,   vCfgTeam, vTeamName,
                @event.Headshot, round);
        }
        else
        {
            // World kill / suicide — still record the death
            _matchManager.RecordKill(
                0, "", 0, "",
                victim.SteamID, victim.PlayerName, vCfgTeam, vTeamName,
                false, round);
        }

        // Scoreboard: credit assist (and flash assist)
        ulong? assisterSteamId = null;
        string? assisterName = null;
        if (assister != null && assister.IsValid && assister != victim)
        {
            assisterSteamId = assister.SteamID;
            assisterName    = assister.PlayerName;
            int asCfgTeam   = _matchManager.GetConfigTeamForPlayer(assister.SteamID);
            string asTeamName = _matchManager.GetTeamNameForConfigTeam(asCfgTeam);
            _matchManager.RecordAssist(assister.SteamID, assister.PlayerName, asCfgTeam, asTeamName,
                @event.Assistedflash, round);
        }

        var vPos = victim.PlayerPawn?.Value?.AbsOrigin;
        float? vx = null, vy = null, vz = null;
        if (vPos != null) { vx = vPos.X; vy = vPos.Y; vz = vPos.Z; }

        _ = _db.LogKillAsync(new KillEventData(
            ctx.Config.MatchId, round,
            attackerSteamId, attackerName, attackerTeam,
            victim.SteamID, victim.PlayerName, victim.TeamNum,
            assisterSteamId, assisterName, @event.Assistedflash,
            @event.Weapon, @event.Headshot,
            @event.Thrusmoke, @event.Attackerblind, @event.Noscope,
            @event.Penetrated > 0, @event.Attackerinair,
            @event.DmgHealth, @event.DmgArmor,
            ax, ay, az, vx, vy, vz
        ));

        return HookResult.Continue;
    }

    // -------------------------------------------------------------------------
    // Damage / util / flash / bomb
    // -------------------------------------------------------------------------

    public HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        var ctx = _matchManager.Context;
        if (ctx?.State != MatchState.Live) return HookResult.Continue;

        var victim   = @event.Userid;
        var attacker = @event.Attacker;

        if (victim == null || !victim.IsValid) return HookResult.Continue;
        if (attacker == null || !attacker.IsValid || attacker == victim) return HookResult.Continue;

        int round = _matchManager.GetCurrentRound();
        string weapon = @event.Weapon ?? "";

        int aCfgTeam  = _matchManager.GetConfigTeamForPlayer(attacker.SteamID);
        string aTeamName = _matchManager.GetTeamNameForConfigTeam(aCfgTeam);
        int vCfgTeam  = _matchManager.GetConfigTeamForPlayer(victim.SteamID);
        string vTeamName = _matchManager.GetTeamNameForConfigTeam(vCfgTeam);

        // Friendly fire: notify both parties but skip stat recording entirely.
        // RecordDamage still needs to be called to keep HP tracking accurate
        // (a FF hit reduces victim's tracked HP, so the next enemy hit caps correctly).
        if (aCfgTeam != 0 && aCfgTeam == vCfgTeam)
        {
            if (@event.DmgHealth > 0)
            {
                // Show actual HP lost, not raw overkill damage, for accurate feedback.
                int ffActual = Math.Min(@event.DmgHealth, victim.PlayerPawn?.Value?.Health + @event.DmgHealth ?? @event.DmgHealth);
                attacker.PrintToChat(
                    $" \x07[FF]\x01 You dealt \x02{ffActual}\x01 damage to \x09{victim.PlayerName}\x01 with \x0B{weapon}\x01");
                victim.PrintToChat(
                    $" \x07[FF]\x01 \x09{attacker.PlayerName}\x01 dealt \x02{ffActual}\x01 damage to you with \x0B{weapon}\x01");
            }
            // RecordDamage with FF: updates HP tracking but skips stats (enemyDamage=false).
            _matchManager.RecordDamage(
                attacker.SteamID, attacker.PlayerName, aCfgTeam, aTeamName,
                victim.SteamID,   victim.PlayerName,   vCfgTeam, vTeamName,
                weapon, @event.DmgHealth, @event.DmgArmor, round);
            return HookResult.Continue;
        }

        // Enemy damage — pass raw DmgHealth; RecordDamage caps via HP tracking.
        _matchManager.RecordDamage(
            attacker.SteamID, attacker.PlayerName, aCfgTeam, aTeamName,
            victim.SteamID,   victim.PlayerName,   vCfgTeam, vTeamName,
            weapon, @event.DmgHealth, @event.DmgArmor, round);

        return HookResult.Continue;
    }

    public HookResult OnPlayerBlind(EventPlayerBlind @event, GameEventInfo info)
    {
        var ctx = _matchManager.Context;
        if (ctx?.State != MatchState.Live) return HookResult.Continue;

        var victim   = @event.Userid;
        var attacker = @event.Attacker;

        if (victim == null || !victim.IsValid) return HookResult.Continue;
        if (attacker == null || !attacker.IsValid || attacker == victim) return HookResult.Continue;
        if (@event.BlindDuration < 0.1f) return HookResult.Continue; // filter near-zero flashes

        int round = _matchManager.GetCurrentRound();
        int aCfgTeam  = _matchManager.GetConfigTeamForPlayer(attacker.SteamID);
        string aTeamName = _matchManager.GetTeamNameForConfigTeam(aCfgTeam);
        int vCfgTeam  = _matchManager.GetConfigTeamForPlayer(victim.SteamID);

        _matchManager.RecordFlash(
            attacker.SteamID, attacker.PlayerName, aCfgTeam, aTeamName,
            vCfgTeam, @event.BlindDuration, round);

        return HookResult.Continue;
    }

    public HookResult OnGrenadeThrown(EventGrenadeThrown @event, GameEventInfo info)
    {
        var ctx = _matchManager.Context;
        if (ctx?.State != MatchState.Live) return HookResult.Continue;

        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;

        int round = _matchManager.GetCurrentRound();
        int cfgTeam = _matchManager.GetConfigTeamForPlayer(player.SteamID);
        string teamName = _matchManager.GetTeamNameForConfigTeam(cfgTeam);
        _matchManager.RecordGrenade(player.SteamID, player.PlayerName, cfgTeam, teamName, round, @event.Weapon ?? "");

        return HookResult.Continue;
    }

    public HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        var ctx = _matchManager.Context;
        if (ctx?.State != MatchState.Live) return HookResult.Continue;

        var player = @event.Userid;
        if (player == null || !player.IsValid) return HookResult.Continue;

        int round = _matchManager.GetCurrentRound();
        int cfgTeam = _matchManager.GetConfigTeamForPlayer(player.SteamID);
        string teamName = _matchManager.GetTeamNameForConfigTeam(cfgTeam);
        _matchManager.RecordBombPlant(player.SteamID, player.PlayerName, cfgTeam, teamName, round);

        _ = _db.LogRoundEventAsync(new RoundEventData(
            ctx.Config.MatchId, round, "bomb_planted",
            ctx.Team1Score, ctx.Team2Score, null));

        return HookResult.Continue;
    }

    public HookResult OnBombDefused(EventBombDefused @event, GameEventInfo info)
    {
        var ctx = _matchManager.Context;
        if (ctx?.State != MatchState.Live) return HookResult.Continue;

        var player = @event.Userid;
        if (player == null || !player.IsValid) return HookResult.Continue;

        int round = _matchManager.GetCurrentRound();
        int cfgTeam = _matchManager.GetConfigTeamForPlayer(player.SteamID);
        string teamName = _matchManager.GetTeamNameForConfigTeam(cfgTeam);
        _matchManager.RecordBombDefuse(player.SteamID, player.PlayerName, cfgTeam, teamName, round);

        int attempts = _matchManager.GetDefuseAttempts(player.SteamID);
        float timeLeft = _matchManager.GetBombTimeLeft();
        _ = _db.LogRoundEventAsync(new RoundEventData(
            ctx.Config.MatchId, round, "bomb_defused",
            ctx.Team1Score, ctx.Team2Score, null,
            $"{{\"defuser\":\"{player.PlayerName}\",\"steam_id\":\"{player.SteamID}\"," +
            $"\"attempts\":{attempts},\"time_left\":{timeLeft:F2}}}"));

        return HookResult.Continue;
    }

    public HookResult OnBombBeginDefuse(EventBombBegindefuse @event, GameEventInfo info)
    {
        var ctx = _matchManager.Context;
        if (ctx?.State != MatchState.Live) return HookResult.Continue;
        var player = @event.Userid;
        if (player == null || !player.IsValid) return HookResult.Continue;
        _matchManager.RecordDefuseAttempt(player.SteamID);
        return HookResult.Continue;
    }

    public HookResult OnBombExploded(EventBombExploded @event, GameEventInfo info)
    {
        var ctx = _matchManager.Context;
        if (ctx?.State != MatchState.Live) return HookResult.Continue;

        int round = _matchManager.GetCurrentRound();
        int attempts = _matchManager.GetTotalDefuseAttempts();
        _ = _db.LogRoundEventAsync(new RoundEventData(
            ctx.Config.MatchId, round, "bomb_exploded",
            ctx.Team1Score, ctx.Team2Score, null,
            $"{{\"defuse_attempts\":{attempts}}}"));
        return HookResult.Continue;
    }

    // -------------------------------------------------------------------------
    // Shots fired / economy
    // -------------------------------------------------------------------------

    public HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        var ctx = _matchManager.Context;
        if (ctx?.State != MatchState.Live) return HookResult.Continue;
        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;
        int cfgTeam = _matchManager.GetConfigTeamForPlayer(player.SteamID);
        string teamName = _matchManager.GetTeamNameForConfigTeam(cfgTeam);
        int round = _matchManager.GetCurrentRound();
        _matchManager.RecordShotFired(player.SteamID, player.PlayerName, cfgTeam, teamName, round);
        return HookResult.Continue;
    }

    public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var ctx = _matchManager.Context;
        if (ctx?.State != MatchState.Live) return HookResult.Continue;
        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;

        // Safety net: if CS2's spawn-side selection placed the player on the
        // wrong side (e.g. after a respawn race condition), correct it via
        // the enforcement manager so the switch is pre-authorized and the
        // dynamic Team1Side/Team2Side mapping stays the source of truth.
        _matchManager.Enforcement.EnforceSide(player, ctx);

        int cfgTeam = _matchManager.GetConfigTeamForPlayer(player.SteamID);
        string teamName = _matchManager.GetTeamNameForConfigTeam(cfgTeam);
        int round = _matchManager.GetCurrentRound();
        _matchManager.RecordPlayerSpawn(player.SteamID, player.PlayerName, cfgTeam, teamName, round);
        return HookResult.Continue;
    }

    // -------------------------------------------------------------------------
    // Chicken kills
    // -------------------------------------------------------------------------

    public HookResult OnRoundMvp(EventRoundMvp @event, GameEventInfo info)
    {
        var ctx = _matchManager.Context;
        if (ctx?.State != MatchState.Live) return HookResult.Continue;
        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;
        int cfgTeam = _matchManager.GetConfigTeamForPlayer(player.SteamID);
        string teamName = _matchManager.GetTeamNameForConfigTeam(cfgTeam);
        _matchManager.RecordMvp(player.SteamID, player.PlayerName, cfgTeam, teamName);
        return HookResult.Continue;
    }

    public HookResult OnOtherDeath(EventOtherDeath @event, GameEventInfo info)
    {
        if (!string.Equals(@event.Othertype, "chicken", StringComparison.OrdinalIgnoreCase))
            return HookResult.Continue;

        var ctx = _matchManager.Context;
        // Allow during any active match state so chickens are always announced
        if (ctx == null) return HookResult.Continue;

        var killer = Utilities.GetPlayerFromUserid(@event.Attacker);
        if (killer == null || !killer.IsValid || killer.IsBot) return HookResult.Continue;

        int round = _matchManager.GetCurrentRound();
        string weapon = string.IsNullOrEmpty(@event.Weapon) ? "unknown" : @event.Weapon;

        BroadcastAll($" \x04[Match]\x01 \x02{killer.PlayerName}\x01 killed a chicken with \x09{weapon}\x01 (round \x09{round}\x01)");

        if (ctx.State == MatchState.Live)
        {
            _ = _db.LogChickenKillAsync(new ChickenKillData(
                ctx.Config.MatchId, round,
                killer.SteamID, killer.PlayerName,
                killer.TeamNum, weapon));
        }

        return HookResult.Continue;
    }

    // -------------------------------------------------------------------------
    // Chat: registered via AddCommandListener("say") and AddCommandListener("say_team")
    // -------------------------------------------------------------------------

    public HookResult OnPlayerSay(CCSPlayerController? player, CommandInfo info)
    {
        return HandleSay(player, info, teamOnly: false);
    }

    public HookResult OnPlayerSayTeam(CCSPlayerController? player, CommandInfo info)
    {
        return HandleSay(player, info, teamOnly: true);
    }

    private HookResult HandleSay(CCSPlayerController? player, CommandInfo info, bool teamOnly)
    {
        Console.WriteLine($"[CS2Match] HandleSay called: player={player?.PlayerName}, isBot={player?.IsBot}, isValid={player?.IsValid}");
        
        if (player == null || !player.IsValid || player.IsBot)
        {
            Console.WriteLine("[CS2Match] HandleSay: skipping - player is null, not valid, or bot");
            return HookResult.Continue;
        }

        string message = info.GetArg(1);
        Console.WriteLine($"[CS2Match] HandleSay: raw message='{message}'");
        if (string.IsNullOrWhiteSpace(message))
        {
            Console.WriteLine("[CS2Match] HandleSay: skipping - message is empty");
            return HookResult.Continue;
        }

        // Route to player commands
        bool wasCommandHandled = _playerCommands.HandleChatMessage(player, message, teamOnly);
        Console.WriteLine($"[CS2Match] HandleSay: HandleChatMessage returned {wasCommandHandled}");
        
        // Log to DB if a match is active
        var ctx = _matchManager.Context;
        if (ctx != null)
        {
            int round = _matchManager.GetCurrentRound();
            _ = _db.LogChatAsync(new ChatEventData(
                ctx.Config.MatchId, round,
                player.SteamID, player.PlayerName,
                message, teamOnly
            ));
        }

        return HookResult.Continue;
    }

    // -------------------------------------------------------------------------
    // Team enforcement — intercepts the "jointeam" console command (M menu)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Intercepts the "jointeam N" console command (N: 1=spec, 2=T, 3=CT)
    /// fired by the M-menu. This is the *front line* of anti-cheat: every
    /// player-initiated team change passes through here first. EventPlayerTeam
    /// is a backstop for changes that bypass this listener.
    ///
    /// AIM mode (no match context): no restrictions — players freely pick a side.
    ///
    /// Live / Paused (match in progress):
    ///   • Hard block any switch to the opposing playing side.
    ///   • Spectator transitions are allowed; the EnforceSide call on
    ///     reconnect/respawn will pull them back onto the right side.
    ///
    /// Warmup / Knife / SidePick:
    ///   • Registered players are routed to the side currently held by their
    ///     internal team (Team1Side/Team2Side, default T/CT pre-knife).
    ///   • Unregistered players are blocked outright.
    /// </summary>
    public HookResult OnJoinTeam(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid || player.IsBot)
            return HookResult.Continue;

        var ctx = _matchManager.Context;

        // AIM mode — no restrictions on team selection
        if (ctx == null) return HookResult.Continue;

        if (!int.TryParse(info.GetArg(1), out int requestedTeam))
            return HookResult.Continue;

        // Spectator / unassigned transitions are tolerated in every state.
        // Any return-to-play attempt is re-validated below.
        if (requestedTeam <= (int)TeamSide.Spectator)
            return HookResult.Continue;

        var enforcement = _matchManager.Enforcement;

        // Unregistered players cannot join either playing side at any time.
        if (!enforcement.IsRegistered(player.SteamID))
        {
            player.PrintToChat(" \x02[Match]\x01 You are not registered in this match.");
            return HookResult.Stop;
        }

        var expected = enforcement.GetExpectedSide(player.SteamID, ctx);
        if (expected == TeamSide.None) return HookResult.Continue;

        // Live session: any deviation from expected side is treated as an
        // anti-cheat / abuse attempt.
        if (ctx.State == MatchState.Live || ctx.State == MatchState.Paused)
        {
            if (requestedTeam != (int)expected)
            {
                player.PrintToChat(" \x02[Match]\x01 You cannot switch sides during a live match.");
                return HookResult.Stop;
            }
            return HookResult.Continue;
        }

        // Warmup / Knife / SidePick: lock to the side currently assigned to
        // the player's internal team. Team1Side/Team2Side default to T/CT
        // pre-knife and are flipped by the side pick.
        if (requestedTeam != (int)expected)
        {
            string assignedName = expected == TeamSide.CounterTerrorist ? "CT" : "T";
            int internalTeam = enforcement.GetInternalTeam(player.SteamID);
            string configTeamName = internalTeam == 1 ? ctx.Config.Team1.Name : ctx.Config.Team2.Name;
            player.PrintToChat($" \x02[Match]\x01 {configTeamName} plays as \x09{assignedName}\x01 — please join that side.");
            return HookResult.Stop;
        }

        return HookResult.Continue;
    }

    // -------------------------------------------------------------------------
    // Backstop team-change enforcement
    // -------------------------------------------------------------------------

    /// <summary>
    /// EventPlayerTeam fires for every team transition the engine processes:
    /// jointeam, mp_restartgame, halftime swap, overtime swap, disconnect, etc.
    /// This handler is the safety net for changes that bypass OnJoinTeam.
    ///
    /// Engine vs player discrimination uses <c>@event.Silent</c>:
    ///   • The engine sets Silent=true for all server-driven moves —
    ///     halftime swap, overtime swap, mp_restartgame, the welcome
    ///     placement after a connect, mp_swapteams, etc. None of these
    ///     should be reverted, and historically the plugin DID revert
    ///     them, blocking the OT side swap entirely.
    ///   • Player-driven moves (M-menu jointeam, console "jointeam N",
    ///     team-select via UI) come through with Silent=false.
    ///
    /// Decision flow:
    ///   1. Bot / null / disconnect → ignore.
    ///   2. AIM mode (no context) → allow.
    ///   3. Unregistered player → ignore (handled by OnJoinTeam).
    ///   4. Plugin-authorized switch (token present) → consume + allow.
    ///   5. Engine silent swap → allow, AND if it moved a registered
    ///      player to the opposite side from what we tracked, flip
    ///      Team1Side/Team2Side once so the internal mapping follows
    ///      the engine. This is the key fix for overtime: the engine's
    ///      OT swap is silent, and after it our Team1Side/Team2Side now
    ///      match reality on the very next event.
    ///   6. Going to spectator → allow.
    ///   7. Side matches expected → allow.
    ///   8. Otherwise → unauthorized player swap; revert + notify.
    /// </summary>
    public HookResult OnPlayerChangeTeam(EventPlayerTeam @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot)
            return HookResult.Continue;

        // Disconnect-driven team change — never react.
        if (@event.Disconnect) return HookResult.Continue;

        var ctx = _matchManager.Context;
        if (ctx == null) return HookResult.Continue; // AIM mode

        var enforcement = _matchManager.Enforcement;
        if (!enforcement.IsRegistered(player.SteamID))
            return HookResult.Continue;

        // Plugin-initiated switch — consume the one-shot token and allow.
        if (enforcement.ConsumeAuthorization(player.SteamID))
            return HookResult.Continue;

        int newTeam = @event.Team;
        int oldTeam = @event.Oldteam;

        // -----------------------------------------------------------------
        // Engine-initiated silent swap (halftime, overtime, restartgame,
        // welcome placement). Trust the engine and update internal mapping
        // if it moved the player off the side we had tracked.
        // -----------------------------------------------------------------
        if (@event.Silent)
        {
            // Only react when moving between two playing sides — joining
            // from spectator (welcome) does not imply a global side flip.
            bool fromPlay = oldTeam == (int)TeamSide.CounterTerrorist || oldTeam == (int)TeamSide.Terrorist;
            bool toPlay   = newTeam == (int)TeamSide.CounterTerrorist || newTeam == (int)TeamSide.Terrorist;

            if (fromPlay && toPlay && newTeam != oldTeam)
            {
                var trackedExpected = enforcement.GetExpectedSide(player.SteamID, ctx);
                if (trackedExpected != TeamSide.None && newTeam != (int)trackedExpected)
                {
                    // Engine moved this player to the opposite side from
                    // what we tracked → our Team1Side/Team2Side mapping is
                    // stale (e.g. OT swap). Flip it once. The flip is
                    // idempotent across the rest of the silent swap burst:
                    // subsequent silent events for other players will hit
                    // the "already matches" branch and pass through.
                    _matchManager.OnEngineSideSwap();
                }
            }
            return HookResult.Continue;
        }

        // Going to spectator/unassigned is tolerated (rejoin will be enforced).
        if (newTeam <= (int)TeamSide.Spectator)
            return HookResult.Continue;

        var expected = enforcement.GetExpectedSide(player.SteamID, ctx);
        if (expected == TeamSide.None) return HookResult.Continue;

        if (newTeam == (int)expected)
            return HookResult.Continue;

        // Unauthorized player-initiated move to the wrong playing side.
        // Schedule revert next frame so the engine has finished applying
        // the original change first.
        var captured = player;
        Server.NextFrame(() =>
        {
            if (captured == null || !captured.IsValid) return;
            var currentCtx = _matchManager.Context;
            if (currentCtx == null) return;
            var nowExpected = _matchManager.Enforcement.GetExpectedSide(captured.SteamID, currentCtx);
            if (nowExpected == TeamSide.None) return;
            _matchManager.Enforcement.ForceSwitch(captured, nowExpected);
            captured.PrintToChat(" \x02[Match]\x01 You cannot switch to the opposing team during a match.");
        });

        return HookResult.Continue;
    }

    // -------------------------------------------------------------------------
    // Match complete / win panel
    // -------------------------------------------------------------------------

    public HookResult OnCsWinPanelMatch(EventCsWinPanelMatch @event, GameEventInfo info)
    {
        var ctx = _matchManager.Context;
        if (ctx == null) return HookResult.Continue;
        _ = _db.LogMatchEventAsync(ctx.Config.MatchId, "win_panel_shown");
        // CS2 fired the win panel — the map is definitively over (including overtime).
        // Trigger map win now so overtime endings are handled correctly.
        _matchManager.OnMapWin();
        return HookResult.Continue;
    }

    // -------------------------------------------------------------------------
    // Player connect
    // -------------------------------------------------------------------------

    public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || player.IsBot) return HookResult.Continue;

        var ctx = _matchManager.Context;
        // AIM mode — let the player join freely
        if (ctx == null) return HookResult.Continue;

        var enforcement = _matchManager.Enforcement;

        // Players not in the match config are kicked outright.
        if (!enforcement.IsRegistered(player.SteamID))
        {
            player.PrintToChat($" \x04[Match]\x01 You are not registered in this match. Disconnecting...");
            Server.ExecuteCommand($"kickid {player.UserId} \"You are not registered in this match.\"");
            return HookResult.Continue;
        }

        int internalTeam = enforcement.GetInternalTeam(player.SteamID);
        string teamName = internalTeam == 1 ? ctx.Config.Team1.Name : ctx.Config.Team2.Name;
        player.PrintToChat($" \x04[Match]\x01 Welcome, \x09{player.PlayerName}\x01! You are on \x0B{teamName}\x01.");

        if (ctx.State == MatchState.Warmup)
            player.PrintToChat($" \x04[Match]\x01 Type \x09!ready\x01 when you are ready to play.");

        // Force the player onto their team's *current* side. Critical for
        // reconnects mid-live: if Team A is now on CT due to halftime/overtime,
        // the player lands directly on CT — no manual jointeam required.
        // Defer one frame so the controller is fully spawned before SwitchTeam.
        var captured = player;
        Server.NextFrame(() =>
        {
            if (captured == null || !captured.IsValid) return;
            var currentCtx = _matchManager.Context;
            if (currentCtx == null) return;
            _matchManager.Enforcement.EnforceSide(captured, currentCtx);
        });

        return HookResult.Continue;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static void BroadcastAll(string message)
    {
        foreach (var player in Utilities.GetPlayers())
        {
            if (player.IsValid && !player.IsBot)
                player.PrintToChat(message);
        }
    }
}
