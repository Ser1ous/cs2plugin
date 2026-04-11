using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CS2MatchPlugin.Config;
using CS2MatchPlugin.Services;

namespace CS2MatchPlugin.Managers;

public class AimManager
{
    private readonly MapChanger _mapChanger;
    private readonly CfgExecutor _cfgExecutor;
    private readonly GameModeSwitcher _gameModeSwitcher;
    private readonly BasePlugin _plugin;
    private PluginConfig? _config;

    /// <summary>
    /// Loop guard. Set to true the instant EnterAimMode() schedules the
    /// AIM map reload; cleared by OnAimMapStart() once the reloaded map
    /// actually starts. While true, follow-up calls to EnterAimMode() and
    /// the on-connect reload path become cheap no-ops, so we never stack
    /// changelevels and end up in a loop.
    /// </summary>
    private bool _isModeApplying = false;

    /// <summary>
    /// True once we've successfully applied the AIM cfg + freezetime
    /// override on the currently-loaded map. Reset by OnAimMapStart()
    /// whenever a new AIM map loads. Used to make EnsureAimApplied()
    /// idempotent so it can be called cheaply from multiple hooks
    /// (OnFirstRoundStart, OnPlayerConnectFull) without re-exec'ing
    /// the cfg every round or every connect.
    /// </summary>
    private bool _aimConfigApplied = false;

    /// <summary>
    /// Freezetime used by AIM mode. Hard-coded at 2s per product spec —
    /// fast enough for aim practice rotations without being instant.
    /// Also written into aim.cfg, but kept here as a belt-and-braces
    /// override the manager can re-apply at any time.
    /// </summary>
    public const int AimFreezeTime = 2;

    /// <summary>
    /// Default competitive freezetime we fall back to when leaving AIM
    /// mode, in case warmup.cfg / competitive.cfg aren't applied yet.
    /// Overwritten by the match's own cvars as soon as the live cfg
    /// executes.
    /// </summary>
    public const int LiveFreezeTimeDefault = 15;

    public AimManager(
        MapChanger mapChanger,
        CfgExecutor cfgExecutor,
        GameModeSwitcher gameModeSwitcher,
        BasePlugin plugin)
    {
        _mapChanger = mapChanger;
        _cfgExecutor = cfgExecutor;
        _gameModeSwitcher = gameModeSwitcher;
        _plugin = plugin;
    }

    public void Configure(PluginConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Entry point that kicks the server into AIM mode. The plugin no
    /// longer touches game_type/game_mode — whatever the server boots
    /// with is left alone. We just reload the AIM map (so aim.cfg
    /// re-runs cleanly) and rely on aim.cfg + ForceAimUnpauseSequence
    /// to clear any pause state.
    ///
    /// Sequence:
    ///   1. Loop-guard check — if we're already applying the mode,
    ///      drop the call (prevents stacked changelevels).
    ///   2. Run the unpause cvar burst NOW so any leftover pause from a
    ///      prior live match doesn't survive into the AIM map.
    ///   3. Schedule the delayed map change (1.0s) via the cancellable
    ///      helper so rapid repeats don't stack reloads.
    /// </summary>
    public void EnterAimMode()
    {
        if (_config == null) return;

        if (_isModeApplying)
        {
            Console.WriteLine(
                "[CS2Match] EnterAimMode: already applying mode, skipping " +
                "(loop guard)");
            return;
        }

        Console.WriteLine($"[CS2Match] Entering AIM mode → {_config.AimMapName}");

        _isModeApplying = true;

        _gameModeSwitcher.MapChangeDelayed(
            _config.AimMapName,
            label: "AIM",
            delaySeconds: 1.0f,
            onBeforeDelay: ForceAimUnpauseSequence);
    }

    /// <summary>
    /// Cvar burst that clears any pause state and locks AIM freezetime.
    /// Separated out so OnMapStart can call it (defence in depth) without
    /// triggering another reload. aim.cfg sets these too — this is a
    /// safety net for the moment between EnterAimMode() being called and
    /// aim.cfg actually running on the new map.
    /// </summary>
    public void ForceAimUnpauseSequence()
    {
        Console.WriteLine("[CS2Match] ForceAimUnpauseSequence: clearing pause + locking AIM cvars");

        // Crush any lingering pause state from a previous match.
        //   mp_warmup_pausetimer 0 — warmup timer will actually tick
        //   mp_unpause_match    — drops mp_pause_match if it was set
        //   sv_pausable 0       — disable manual pause keybinds
        Server.ExecuteCommand("mp_warmup_pausetimer 0");
        Server.ExecuteCommand("mp_unpause_match");
        Server.ExecuteCommand("sv_pausable 0");

        // End any leftover warmup immediately so the server doesn't
        // sit in a frozen ready-up screen.
        Server.ExecuteCommand("mp_warmup_end");

        // AIM freezetime lock (spec: 2s)
        Server.ExecuteCommand($"mp_freezetime {AimFreezeTime}");

        Console.WriteLine("[CS2Match] ForceAimUnpauseSequence: complete");
    }

    /// <summary>
    /// Called by MatchManager.OnMapStart whenever the server loads a
    /// map while Context == null (i.e. AIM mode, not a match). Clears
    /// the "already applied" flag so the next call to
    /// EnsureAimApplied() — from either a round start or a player
    /// connect — re-executes the cfg on the fresh map. Also clears
    /// the loop guard so a subsequent EnterAimMode() (e.g. after a
    /// match ends on a different map) can proceed, AND re-runs the
    /// unpause cvar burst as defence-in-depth against any
    /// pausetimer state that survived the map load.
    /// </summary>
    public void OnAimMapStart()
    {
        _aimConfigApplied = false;
        _isModeApplying   = false;

        // Belt-and-braces: re-assert pause-disable cvars on the freshly
        // loaded AIM map. These are cheap, idempotent, and protect
        // against the stuck-pause symptom the task is fixing.
        Server.ExecuteCommand("mp_warmup_pausetimer 0");
        Server.ExecuteCommand("sv_pausable 0");
        Server.ExecuteCommand("mp_unpause_match");
    }

    /// <summary>
    /// Idempotent apply-if-needed helper. Safe to call from any hook
    /// that might fire while the server is in AIM mode (RoundStart,
    /// PlayerConnectFull). First caller on a given map actually runs
    /// the cfg + sets mp_freezetime; subsequent callers are cheap no-ops.
    ///
    /// Returns true if this call actually ran ApplyAimConfig, false if
    /// the cfg was already applied on this map.
    /// </summary>
    public bool EnsureAimApplied()
    {
        if (_aimConfigApplied) return false;
        ApplyAimConfig();
        return true;
    }

    public void ApplyAimConfig()
    {
        if (_config == null) return;
        Console.WriteLine($"[CS2Match] Applying AIM config: {_config.AimCfgName}");
        _cfgExecutor.ExecCfg(_config.AimCfgName);

        // Force AIM-mode freezetime to exactly 2 seconds. Executed AFTER
        // the cfg so it overrides whatever value the cfg file carries —
        // this keeps the spec-mandated 2s locked-in even if someone edits
        // aim.cfg locally. Only AIM sessions see this; StartLive() runs
        // competitive.cfg which sets its own freezetime and then applies
        // the JSON cvars, cleanly overwriting the AIM value.
        Server.ExecuteCommand($"mp_freezetime {AimFreezeTime}");
        Console.WriteLine($"[CS2Match] AIM mp_freezetime locked to {AimFreezeTime}s");

        _aimConfigApplied = true;
    }

    /// <summary>
    /// Forcibly re-applies mp_freezetime 2 without re-running the full
    /// cfg. Used when the first player joins an AIM map that hasn't
    /// had a round start yet — guarantees the player's very first
    /// freezetime is 2s, not whatever value (usually 15) the server
    /// booted with.
    /// </summary>
    public void ForceAimFreezetimeNow()
    {
        Console.WriteLine($"[CS2Match] Forcing AIM mp_freezetime → {AimFreezeTime}s (first connect)");
        Server.ExecuteCommand($"mp_freezetime {AimFreezeTime}");
    }

    /// <summary>
    /// Explicitly reverts any AIM-only server state when transitioning
    /// out of AIM mode into a real match. Called by MatchManager before
    /// loading a new match config so the AIM freezetime (and any other
    /// AIM-only overrides we might add) never leak into warmup/live.
    /// </summary>
    public void ClearAimOverrides()
    {
        Console.WriteLine($"[CS2Match] Clearing AIM overrides (freezetime → {LiveFreezeTimeDefault}s)");
        Server.ExecuteCommand($"mp_freezetime {LiveFreezeTimeDefault}");
        _aimConfigApplied = false;
    }

    public bool IsAimMap(string mapName)
    {
        if (_config == null) return false;
        return string.Equals(mapName, _config.AimMapName, StringComparison.OrdinalIgnoreCase);
    }
}
