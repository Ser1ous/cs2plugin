using CS2MatchPlugin.Config;

namespace CS2MatchPlugin.Managers;

public class PauseManager
{
    private readonly HashSet<ulong> _unpauseVotes = new();
    private bool _team1UnpauseVoted = false;
    private bool _team2UnpauseVoted = false;
    private MatchConfig? _config;

    public event Action? OnBothTeamsUnpaused;

    public void Setup(MatchConfig config)
    {
        _config = config;
        Reset();
    }

    public void Reset()
    {
        _unpauseVotes.Clear();
        _team1UnpauseVoted = false;
        _team2UnpauseVoted = false;
    }

    public void RegisterUnpause(ulong steamId)
    {
        if (_config == null) return;
        if (_unpauseVotes.Contains(steamId)) return;

        _unpauseVotes.Add(steamId);

        if (_config.Team1.Players.ContainsKey(steamId.ToString())) _team1UnpauseVoted = true;
        if (_config.Team2.Players.ContainsKey(steamId.ToString())) _team2UnpauseVoted = true;

        if (_team1UnpauseVoted && _team2UnpauseVoted)
            OnBothTeamsUnpaused?.Invoke();
    }

    public (bool team1, bool team2) GetUnpauseStatus() => (_team1UnpauseVoted, _team2UnpauseVoted);

    public bool IsRegisteredPlayer(ulong steamId)
    {
        if (_config == null) return false;
        string sid = steamId.ToString();
        return _config.Team1.Players.ContainsKey(sid) || _config.Team2.Players.ContainsKey(sid);
    }
}
