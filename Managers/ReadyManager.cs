using CS2MatchPlugin.Config;

namespace CS2MatchPlugin.Managers;

public class ReadyManager
{
    private readonly HashSet<ulong> _readyPlayers = new();
    private MatchConfig? _config;
    private int _requiredCount;

    public event Action? OnAllReady;

    public void Setup(MatchConfig config)
    {
        _config = config;
        _readyPlayers.Clear();

        int total = 0;
        foreach (var p in config.Team1.Players)
            if (!TeamConfig.IsBotId(p.Key)) total++;
        foreach (var p in config.Team2.Players)
            if (!TeamConfig.IsBotId(p.Key)) total++;
        _requiredCount = total > 0 ? total : config.PlayersPerTeam * 2;
    }

    public void Reset()
    {
        _readyPlayers.Clear();
    }

    /// <summary>Returns true if this player is registered in the match.</summary>
    public bool IsRegisteredPlayer(ulong steamId)
    {
        if (_config == null) return false;
        return _config.Team1.Players.ContainsKey(steamId.ToString()) ||
               _config.Team2.Players.ContainsKey(steamId.ToString());
    }

    public (int ready, int required) GetStatus() => (_readyPlayers.Count, _requiredCount);

    /// <summary>Returns display names of players who have not yet typed !ready.</summary>
    public List<string> GetNotReadyNames()
    {
        if (_config == null) return new();
        var result = new List<string>();
        foreach (var (sid, name) in _config.Team1.Players)
            if (!TeamConfig.IsBotId(sid) && ulong.TryParse(sid, out var id) && !_readyPlayers.Contains(id))
                result.Add(name);
        foreach (var (sid, name) in _config.Team2.Players)
            if (!TeamConfig.IsBotId(sid) && ulong.TryParse(sid, out var id) && !_readyPlayers.Contains(id))
                result.Add(name);
        return result;
    }

    public bool MarkReady(ulong steamId)
    {
        if (!IsRegisteredPlayer(steamId))
            return false;

        _readyPlayers.Add(steamId);

        if (_readyPlayers.Count >= _requiredCount)
            OnAllReady?.Invoke();

        return true;
    }
}
