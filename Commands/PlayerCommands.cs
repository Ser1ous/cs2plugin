using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CS2MatchPlugin.Managers;
using CS2MatchPlugin.State;

namespace CS2MatchPlugin.Commands;

/// <summary>
/// Handles chat-triggered player commands: !ready, .ready, .pause, .unpause, .ct, .t
/// This class processes raw say messages routed from the event handler.
/// </summary>
public class PlayerCommands
{
    private readonly MatchManager _matchManager;

    public PlayerCommands(MatchManager matchManager)
    {
        _matchManager = matchManager;
    }

    /// <summary>
    /// Process a chat message. Returns true if the message was a known command (to suppress chat if desired).
    /// </summary>
    public bool HandleChatMessage(CCSPlayerController player, string message, bool teamOnly)
    {
        string msg = message.Trim().ToLowerInvariant();

        switch (msg)
        {
            case "!ready":
            case ".ready":
                HandleReady(player);
                return true;

            case ".pause":
                HandlePause(player);
                return true;

            case ".unpause":
                HandleUnpause(player);
                return true;

            case ".ct":
                HandleSidePick(player, "ct");
                return true;

            case ".t":
                HandleSidePick(player, "t");
                return true;

            case ".stay":
                HandleSidePick(player, "stay");
                return true;

            case ".switch":
                HandleSidePick(player, "switch");
                return true;

            default:
                return false;
        }
    }

    private void HandleReady(CCSPlayerController player)
    {
        if (_matchManager.State != MatchState.Warmup)
        {
            // Silently ignore outside warmup
            return;
        }
        _matchManager.OnPlayerReady(player);
    }

    private void HandlePause(CCSPlayerController player)
    {
        if (_matchManager.State != MatchState.Live)
        {
            player.PrintToChat(" \x02[Match]\x01 Pause is only available during a live match.");
            return;
        }
        _matchManager.OnPauseRequest(player);
    }

    private void HandleUnpause(CCSPlayerController player)
    {
        if (_matchManager.State != MatchState.Paused)
        {
            player.PrintToChat(" \x02[Match]\x01 Match is not paused.");
            return;
        }
        _matchManager.OnUnpauseVote(player);
    }

    private void HandleSidePick(CCSPlayerController player, string side)
    {
        if (_matchManager.State != MatchState.SidePick)
        {
            // Silently ignore — players may type .ct/.t at other times
            return;
        }
        _matchManager.OnSidePick(player, side);
    }
}
