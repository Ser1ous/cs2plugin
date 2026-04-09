using CS2MatchPlugin.Config;
using CS2MatchPlugin.Services;

namespace CS2MatchPlugin.Managers;

public class AimManager
{
    private readonly MapChanger _mapChanger;
    private readonly CfgExecutor _cfgExecutor;
    private PluginConfig? _config;

    public AimManager(MapChanger mapChanger, CfgExecutor cfgExecutor)
    {
        _mapChanger = mapChanger;
        _cfgExecutor = cfgExecutor;
    }

    public void Configure(PluginConfig config)
    {
        _config = config;
    }

    public void EnterAimMode()
    {
        if (_config == null) return;
        Console.WriteLine($"[CS2Match] Switching to AIM mode: {_config.AimMapName}");
        _mapChanger.ChangeMap(_config.AimMapName, _config.AimGameType, _config.AimGameMode);
    }

    public void ApplyAimConfig()
    {
        if (_config == null) return;
        Console.WriteLine($"[CS2Match] Applying AIM config: {_config.AimCfgName}");
        _cfgExecutor.ExecCfg(_config.AimCfgName);
    }

    public bool IsAimMap(string mapName)
    {
        if (_config == null) return false;
        return string.Equals(mapName, _config.AimMapName, StringComparison.OrdinalIgnoreCase);
    }
}
