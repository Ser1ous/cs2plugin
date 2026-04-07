using CounterStrikeSharp.API;

namespace CS2MatchPlugin.Services;

public class CfgExecutor
{
    public void ExecCfg(string cfgName)
    {
        if (string.IsNullOrWhiteSpace(cfgName))
            return;

        Console.WriteLine($"[CS2Match] Executing config: {cfgName}.cfg");
        Server.ExecuteCommand($"exec {cfgName}.cfg");
    }

    public void ExecCvars(Dictionary<string, string> cvars)
    {
        foreach (var (key, value) in cvars)
        {
            if (!string.IsNullOrWhiteSpace(key))
                Server.ExecuteCommand($"{key} {value}");
        }
    }
}
