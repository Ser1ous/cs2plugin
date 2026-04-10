using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using CS2MatchPlugin.Config;
using CS2MatchPlugin.State;

namespace CS2MatchPlugin.Managers;

/// <summary>
/// Centralizes player ↔ team enforcement for Live (match) mode.
///
/// Two-phase enforcement model:
///   • Pre-Live  (Warmup / Knife / SidePick): players are locked to the
///     CS side currently held by their internal team. Team1Side/Team2Side
///     default to T/CT and are reassigned by the side pick — until then,
///     enforcement uses those defaults.
///   • Live      (Live / Paused): players are locked to their *internal*
///     team (1 or 2). The CS side they should be on is read dynamically
///     from MatchContext.Team1Side / Team2Side, which the MatchManager
///     swaps at halftime (and at overtime swaps once that hook is wired
///     up). Reconnects therefore land on whichever side the team currently
///     occupies — no manual remapping required.
///
/// AIM mode: <see cref="LoadFromConfig"/> is never called, so
/// <see cref="Enabled"/> stays false and every helper short-circuits.
/// All callers MUST first check <see cref="Enabled"/> (or the parent
/// MatchContext being non-null) before invoking enforcement.
///
/// Engine vs player-initiated team changes:
///   The CS2 game engine emits EventPlayerTeam for many things the plugin
///   should not block — halftime swap, overtime swap, mp_restartgame, etc.
///   To distinguish them from player-initiated jointeam attempts we use a
///   one-shot authorization token: any switch performed by the plugin via
///   <see cref="ForceSwitch"/> pre-registers the SteamID, and the
///   EventPlayerTeam handler consumes it. For genuine engine swaps we rely
///   on the fact that Team1Side/Team2Side is updated *before* the engine
///   moves players, so the new side already matches the expected side and
///   the change is allowed naturally.
/// </summary>
public class TeamEnforcementManager
{
    // Permanent SteamID → internal config team (1 or 2) mapping for the
    // duration of one match. Cleared on match end / abort.
    private readonly Dictionary<ulong, int> _playerTeamMap = new();

    // SteamIDs whose next EventPlayerTeam was triggered by the plugin itself.
    // Consumed (removed) on the matching event so unauthorized changes still
    // get caught even after a previous authorized switch.
    private readonly HashSet<ulong> _pendingAuthorized = new();

    // BasePlugin for AddTimer scheduling — required so delayed respawns
    // survive past the NextFrame boundary.
    private readonly BasePlugin _plugin;

    public TeamEnforcementManager(BasePlugin plugin)
    {
        _plugin = plugin;
    }

    /// <summary>True while a match is loaded and enforcement should run.</summary>
    public bool Enabled { get; private set; }

    /// <summary>
    /// Snapshot the JSON config into the internal SteamID → team map.
    /// Call once per match, right after the MatchContext is created.
    /// </summary>
    public void LoadFromConfig(MatchConfig config)
    {
        _playerTeamMap.Clear();
        _pendingAuthorized.Clear();

        foreach (var key in config.Team1.Players.Keys)
            if (ulong.TryParse(key, out var sid))
                _playerTeamMap[sid] = 1;

        foreach (var key in config.Team2.Players.Keys)
            if (ulong.TryParse(key, out var sid))
                _playerTeamMap[sid] = 2;

        Enabled = true;
        Console.WriteLine($"[CS2Match] TeamEnforcement: loaded {_playerTeamMap.Count} players ({config.Team1.Players.Count} / {config.Team2.Players.Count})");
    }

    /// <summary>
    /// Wipe all enforcement state. Call from EndMatch / AbortMatch and any
    /// other path that tears down the MatchContext, otherwise stale entries
    /// would leak into the next match or into AIM mode.
    /// </summary>
    public void Clear()
    {
        _playerTeamMap.Clear();
        _pendingAuthorized.Clear();
        Enabled = false;
        Console.WriteLine("[CS2Match] TeamEnforcement: cleared");
    }

    public bool IsRegistered(ulong steamId) => _playerTeamMap.ContainsKey(steamId);

    public int GetInternalTeam(ulong steamId)
        => _playerTeamMap.TryGetValue(steamId, out var team) ? team : 0;

    /// <summary>
    /// Returns the CS side (CT/T) the player is currently expected to occupy,
    /// based on their internal team and the live Team1Side/Team2Side mapping.
    /// </summary>
    public TeamSide GetExpectedSide(ulong steamId, MatchContext ctx) => GetInternalTeam(steamId) switch
    {
        1 => ctx.Team1Side,
        2 => ctx.Team2Side,
        _ => TeamSide.None,
    };

    /// <summary>
    /// Mark the next team change for this SteamID as plugin-authorized.
    /// Always paired with <see cref="ConsumeAuthorization"/> in the event handler.
    /// </summary>
    public void AuthorizeSwitch(ulong steamId) => _pendingAuthorized.Add(steamId);

    /// <summary>
    /// Returns true if a plugin-authorized switch was pending for this SteamID;
    /// the token is consumed in the process.
    /// </summary>
    public bool ConsumeAuthorization(ulong steamId) => _pendingAuthorized.Remove(steamId);

    /// <summary>
    /// Plugin-initiated team switch. Pre-authorizes the change so the
    /// EventPlayerTeam handler does not revert it.
    /// </summary>
    public void ForceSwitch(CCSPlayerController player, TeamSide side)
    {
        if (!player.IsValid || side == TeamSide.None) return;
        AuthorizeSwitch(player.SteamID);
        player.SwitchTeam((CsTeam)(int)side);
    }

    /// <summary>
    /// If the player is on the wrong CS side for their internal team, move
    /// them to the correct one, and ensure they actually spawn into the
    /// round rather than sticking in the spectator shell. No-op for
    /// unregistered players, AIM mode, or when sides are undetermined.
    /// </summary>
    public void EnforceSide(CCSPlayerController player, MatchContext ctx)
    {
        if (!Enabled || !player.IsValid || player.IsBot) return;

        var expected = GetExpectedSide(player.SteamID, ctx);
        if (expected == TeamSide.None) return;

        bool needsSwitch = player.TeamNum != (int)expected;
        if (needsSwitch)
            ForceSwitch(player, expected);

        // Bug fix: SwitchTeam alone leaves the player as a spectator shell
        // until the next round tick. During states that allow mid-phase
        // respawning we explicitly wake them up into a spawn point. Live
        // rounds are skipped — respawning mid-round would be a cheat; the
        // engine will place them at the next round start naturally.
        if (CanRespawnNow(ctx.State))
            ScheduleRespawn(player);
    }

    /// <summary>
    /// States where a late-joiner should be respawned immediately after
    /// being moved to the correct side. Live/Paused are intentionally
    /// excluded: CS2 will spawn the player at the next round_start.
    /// </summary>
    private static bool CanRespawnNow(MatchState state) =>
        state == MatchState.Warmup ||
        state == MatchState.Knife  ||
        state == MatchState.SidePick;

    /// <summary>
    /// Schedules a short-delay respawn. The delay gives the engine enough
    /// time to finish processing the SwitchTeam (team slot reassignment,
    /// pawn re-init) before Respawn() is called — calling both back-to-back
    /// leaves the pawn in an invalid state and the player stays specced.
    ///
    /// A NextFrame attempt is made first; a 0.3s fallback catches cases
    /// where the first attempt no-ops because the pawn isn't ready yet.
    /// </summary>
    private void ScheduleRespawn(CCSPlayerController player)
    {
        var captured = player;

        Server.NextFrame(() => TryRespawn(captured));
        _plugin.AddTimer(0.3f, () => TryRespawn(captured));
    }

    private static void TryRespawn(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid) return;
        // If the player is already alive (first attempt succeeded, or they
        // were re-enforced mid-round), skip — re-spawning an alive pawn
        // teleports them to a spawn point which is not what we want.
        if (player.PawnIsAlive) return;
        // Spectator (team 1) or unassigned (team 0) cannot respawn — the
        // SwitchTeam earlier should have moved them to T/CT; re-check to be safe.
        if (player.TeamNum < (int)TeamSide.Terrorist) return;
        try
        {
            player.Respawn();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CS2Match] TeamEnforcement: Respawn failed for {player.PlayerName}: {ex.Message}");
        }
    }
}
