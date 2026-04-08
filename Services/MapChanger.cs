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

    public void ChangeMap(string mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName))
        {
            Console.WriteLine("[CS2Match] ChangeMap called with empty map name");
            return;
        }

        _plugin.AddTimer(0.5f, () =>
        {
            bool alreadyOnMap = string.Equals(Server.MapName, mapName, StringComparison.OrdinalIgnoreCase);

            if (alreadyOnMap)
            {
                // Already on the correct map — restart in place instead of a full level reload.
                // changelevel to the same map floods clients with packets during the transition
                // and causes "recv queue overflow" disconnects.
                Console.WriteLine($"[CS2Match] Already on {mapName}, using mp_restartgame instead of changelevel");
                Server.ExecuteCommand("mp_restartgame 3");

                // Fire OnMapStart manually after the restart settles so the plugin
                // transitions into warmup exactly as it would after a real map change.
                _plugin.AddTimer(4.0f, () =>
                {
                    Server.ExecuteCommand($"host_say [CS2Match] Map restarted: {mapName}");
                });
            }
            else
            {
                // Workshop maps have a numeric ID — use host_workshop_map instead of changelevel
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
