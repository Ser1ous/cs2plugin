using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace CS2MatchPlugin.Services;

public class MapChanger
{
    private readonly BasePlugin _plugin;

    public MapChanger(BasePlugin plugin)
    {
        _plugin = plugin;
    }

    /// <summary>
    /// Changes the map and sets game_type/game_mode immediately before the
    /// transition command so CS2 reads the correct values in ExecGameTypeCfg.
    /// </summary>
    public void ChangeMap(string mapName, int gameType = 0, int gameMode = 0)
    {
        if (string.IsNullOrWhiteSpace(mapName))
        {
            Console.WriteLine("[CS2Match] ChangeMap called with empty map name");
            return;
        }

        _plugin.AddTimer(0.5f, () =>
        {
            // Set game_type/game_mode immediately before the transition so there
            // is no window for CS2 to reset them. ExecGameTypeCfg reads these
            // ConVars at the moment of changelevel/mp_restartgame.
            Server.ExecuteCommand($"game_type {gameType}");
            Server.ExecuteCommand($"game_mode {gameMode}");

            bool alreadyOnMap = string.Equals(Server.MapName, mapName, StringComparison.OrdinalIgnoreCase);

            if (alreadyOnMap)
            {
                Console.WriteLine($"[CS2Match] Already on {mapName}, using mp_restartgame instead of changelevel");
                Server.ExecuteCommand("mp_restartgame 3");

                _plugin.AddTimer(4.0f, () =>
                {
                    Server.ExecuteCommand($"host_say [CS2Match] Map restarted: {mapName}");
                });
            }
            else
            {
                bool isWorkshop = ulong.TryParse(mapName, out _);
                if (isWorkshop)
                {
                    Console.WriteLine($"[CS2Match] Loading workshop map: {mapName}");
                    Server.ExecuteCommand($"host_workshop_map {mapName}");
                }
                else
                {
                    Console.WriteLine($"[CS2Match] Changing map to: {mapName}");
                    Server.ExecuteCommand($"changelevel {mapName}");
                }
            }
        });
    }
}
