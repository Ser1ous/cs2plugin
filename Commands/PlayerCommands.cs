using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CS2MatchPlugin.Managers;
using CS2MatchPlugin.State;

namespace CS2MatchPlugin.Commands;

/// <summary>
/// Handles chat-triggered player commands: .pause, .unpause, .ct, .t, .stay, .switch.
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
        Console.WriteLine($"[CS2Match] HandleChatMessage: {player.PlayerName} said '{message}' msg='{msg}' teamOnly={teamOnly}");

        switch (msg)
        {
            case ".pause":
                Console.WriteLine($"[CS2Match] HandleChatMessage: MATCHED .pause command");
                HandlePause(player);
                return true;

            case ".unpause":
                Console.WriteLine($"[CS2Match] HandleChatMessage: MATCHED .unpause command");
                HandleUnpause(player);
                return true;

            case "ct":
            case ".ct":
                Console.WriteLine($"[CS2Match] HandleChatMessage: MATCHED .ct/.ct command");
                HandleSidePick(player, "ct");
                return true;

            case "t":
            case ".t":
                Console.WriteLine($"[CS2Match] HandleChatMessage: MATCHED .t/t command");
                HandleSidePick(player, "t");
                return true;

            case "stay":
            case ".stay":
                Console.WriteLine($"[CS2Match] HandleChatMessage: MATCHED .stay/stay command");
                HandleSidePick(player, "stay");
                return true;

            case "switch":
            case ".switch":
                Console.WriteLine($"[CS2Match] HandleChatMessage: MATCHED .switch/switch command");
                HandleSidePick(player, "switch");
                return true;

            default:
                Console.WriteLine($"[CS2Match] HandleChatMessage: no match for '{msg}'");
                return false;
        }
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
        Console.WriteLine($"[CS2Match] HandleSidePick: {player.PlayerName} side={side}");
        Console.WriteLine($"[CS2Match] HandleSidePick: _matchManager.State={_matchManager.State}");
        if (_matchManager.State != MatchState.SidePick)
        {
            Console.WriteLine($"[CS2Match] HandleSidePick: not in SidePick state, skipping");
            // Silently ignore — players may type .ct/.t at other times
            return;
        }
        Console.WriteLine($"[CS2Match] HandleSidePick: calling OnSidePick");
        _matchManager.OnSidePick(player, side);
    }
}
