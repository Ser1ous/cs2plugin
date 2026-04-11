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
    /// Changes the map after a small delay so the engine has time to
    /// flush any in-flight commands.
    ///
    /// Two outcomes:
    ///   1. Already on the requested map → mp_restartgame (cheap path).
    ///   2. Different map                → changelevel / host_workshop_map.
    ///
    /// Workshop-aware: numeric (ulong-parseable) names go through
    /// <c>host_workshop_map</c>, named maps via <c>changelevel</c>.
    /// </summary>
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
            bool isWorkshop   = ulong.TryParse(mapName, out _);

            if (alreadyOnMap)
            {
                Console.WriteLine(
                    $"[CS2Match] Already on {mapName}, using mp_restartgame instead of changelevel");
                Server.ExecuteCommand("mp_restartgame 3");

                _plugin.AddTimer(4.0f, () =>
                {
                    Server.ExecuteCommand($"host_say [CS2Match] Map restarted: {mapName}");
                });
                return;
            }

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
        });
    }
}
