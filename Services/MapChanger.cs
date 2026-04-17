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

        string trimmed = mapName.Trim();

        _plugin.AddTimer(0.5f, () =>
        {
            bool alreadyOnMap = string.Equals(Server.MapName, trimmed, StringComparison.OrdinalIgnoreCase);
            bool isWorkshop   = IsWorkshopId(trimmed);

            if (alreadyOnMap)
            {
                Console.WriteLine(
                    $"[CS2Match] Already on {trimmed}, using mp_restartgame instead of changelevel");
                Server.ExecuteCommand("mp_restartgame 3");

                _plugin.AddTimer(4.0f, () =>
                {
                    Server.ExecuteCommand($"host_say [CS2Match] Map restarted: {trimmed}");
                });
                return;
            }

            if (isWorkshop)
            {
                Console.WriteLine($"[CS2Match] Loading workshop map: {trimmed}");
                Server.ExecuteCommand($"host_workshop_map {trimmed}");
            }
            else
            {
                Console.WriteLine($"[CS2Match] Changing map to: {trimmed}");
                Server.ExecuteCommand($"changelevel {trimmed}");
            }
        });
    }

    public static bool IsWorkshopId(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (char c in value)
        {
            if (c < '0' || c > '9') return false;
        }
        return ulong.TryParse(value, out _);
    }
}
