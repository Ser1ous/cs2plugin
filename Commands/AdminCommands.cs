using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Admin;
using CS2MatchPlugin.Managers;
using CS2MatchPlugin.State;

namespace CS2MatchPlugin.Commands;

public class AdminCommands
{
    private readonly MatchManager _matchManager;
    private readonly AimManager _aimManager;

    public AdminCommands(MatchManager matchManager, AimManager aimManager)
    {
        _matchManager = matchManager;
        _aimManager = aimManager;
    }

    // Registered manually in CS2Plugin.OnLoad via AddCommand
    public void OnLoadUrlCommand(CCSPlayerController? caller, CommandInfo info)
    {
        if (!IsAdmin(caller))
        {
            info.ReplyToCommand("[CS2Match] You do not have permission to run this command.");
            return;
        }

        if (info.ArgCount < 2)
        {
            info.ReplyToCommand("[CS2Match] Usage: ser_plug_load_url <url>");
            return;
        }

        string url = info.GetArg(1);
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            info.ReplyToCommand($"[CS2Match] Invalid URL: {url}");
            return;
        }

        // FORCE-OVERRIDE policy: ser_plug_load_url NEVER rejects an active
        // match. If a new URL arrives while a match is live, warmup, paused,
        // or even mid-knife-round, we treat it as highest priority, tear
        // down state, and replace it with the new config. This lets match
        // coordinators re-push a corrected config without needing to SSH in
        // and rcon an abort first.
        var activeCtx = _matchManager.Context;
        if (activeCtx != null)
        {
            Console.WriteLine(
                $"[CS2Match] ser_plug_load_url: FORCE-OVERRIDE — active match " +
                $"(id={activeCtx.Config.MatchId}, state={activeCtx.State}) will be " +
                $"replaced by new config from {url}");
            info.ReplyToCommand(
                $"[CS2Match] Active match detected (state={activeCtx.State}). " +
                $"Force-overriding with new config...");
        }
        else
        {
            Console.WriteLine($"[CS2Match] ser_plug_load_url: loading fresh config from {url} (no active match)");
        }

        info.ReplyToCommand($"[CS2Match] Loading match config from: {url}");
        _ = _matchManager.LoadMatchFromUrlAsync(url, msg => info.ReplyToCommand($"[CS2Match] {msg}"));
    }

    public void OnAimModeCommand(CCSPlayerController? caller, CommandInfo info)
    {
        if (!IsAdmin(caller))
        {
            info.ReplyToCommand("[CS2Match] You do not have permission to run this command.");
            return;
        }

        _matchManager.AbortMatch();
        _aimManager.EnterAimMode();
        info.ReplyToCommand("[CS2Match] Switching to AIM mode.");
    }

    // ser_aim_mode — cancel an unattended live match and return to AIM mode.
    // Conditions that must ALL be true to act:
    //   1. A match is active AND its state is Live (loaded via ser_plug_load_url).
    //   2. No real (non-bot) players are currently connected.
    // Any other situation is silently ignored — this command is only for
    // cleaning up matches that nobody joined.
    public void OnSerAimModeCommand(CCSPlayerController? caller, CommandInfo info)
    {
        var ctx = _matchManager.Context;

        // Ignore if no active match or match is not in Warmup state.
        if (ctx == null || ctx.State != MatchState.Warmup)
            return;

        // Ignore if any real (non-bot, non-SourceTV) player is still on the server.
        bool anyHumans = Utilities.GetPlayers()
            .Any(p => p.IsValid && !p.IsBot && !p.IsHLTV);

        if (anyHumans)
            return;

        Console.WriteLine("[CS2Match] ser_aim_mode: no human players connected — kicking bots and switching to AIM mode.");
        Server.ExecuteCommand("bot_kick");
        Server.ExecuteCommand("bot_quota 0");
        _matchManager.AbortMatch();
        _aimManager.EnterAimMode();
    }

    public void OnAbortMatchCommand(CCSPlayerController? caller, CommandInfo info)
    {
        if (!IsAdmin(caller))
        {
            info.ReplyToCommand("[CS2Match] You do not have permission to run this command.");
            return;
        }

        if (_matchManager.Context == null)
        {
            info.ReplyToCommand("[CS2Match] No active match to abort.");
            return;
        }

        _matchManager.AbortMatch();
        info.ReplyToCommand("[CS2Match] Match aborted.");
    }

    public void OnMatchStatusCommand(CCSPlayerController? caller, CommandInfo info)
    {
        var ctx = _matchManager.Context;
        if (ctx == null)
        {
            info.ReplyToCommand("[CS2Match] No active match. Server is in AIM mode.");
            return;
        }

        info.ReplyToCommand($"[CS2Match] Match: {ctx.Config.MatchId} | State: {ctx.State}");
        info.ReplyToCommand($"[CS2Match] {ctx.Config.Team1.Name} {ctx.Team1Score} - {ctx.Team2Score} {ctx.Config.Team2.Name}");
        info.ReplyToCommand($"[CS2Match] Map: {ctx.Config.Maplist[ctx.CurrentMapIndex]} ({ctx.CurrentMapIndex + 1}/{ctx.Config.Maplist.Count})");
        info.ReplyToCommand($"[CS2Match] Series: {ctx.MapWinsTeam1}-{ctx.MapWinsTeam2}");
    }

    private static bool IsAdmin(CCSPlayerController? caller)
    {
        // Console (null caller) is always admin
        if (caller == null) return true;
        return AdminManager.PlayerHasPermissions(caller, "@css/root") ||
               AdminManager.PlayerHasPermissions(caller, "@css/admin");
    }
}
