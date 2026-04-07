using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CS2MatchPlugin.State;

namespace CS2MatchPlugin.Managers;

public class KnifeManager
{
    public event Action<TeamSide>? OnKnifeRoundWinner;

    /// <summary>
    /// Called when a round ends during the knife phase.
    /// CS2 EventRoundEnd.Winner: 2=T, 3=CT.
    /// </summary>
    public void HandleRoundEnd(EventRoundEnd @event)
    {
        TeamSide winner = @event.Winner switch
        {
            2 => TeamSide.Terrorist,
            3 => TeamSide.CounterTerrorist,
            _ => DetermineWinnerByAlive()
        };

        Console.WriteLine($"[CS2Match] Knife round ended. Winner: {winner}");
        OnKnifeRoundWinner?.Invoke(winner);
    }

    private static TeamSide DetermineWinnerByAlive()
    {
        int ctAlive = 0, tAlive = 0;
        foreach (var p in Utilities.GetPlayers())
        {
            if (!p.IsValid || !p.PawnIsAlive) continue;
            if (p.TeamNum == (int)TeamSide.CounterTerrorist) ctAlive++;
            else if (p.TeamNum == (int)TeamSide.Terrorist) tAlive++;
        }
        return ctAlive >= tAlive ? TeamSide.CounterTerrorist : TeamSide.Terrorist;
    }
}
