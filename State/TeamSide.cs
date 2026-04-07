namespace CS2MatchPlugin.State;

/// <summary>
/// Mirrors CS2 team numbers: None=0, Spectator=1, T=2, CT=3
/// Defined here to avoid CSS package version issues with CsTeam enum location.
/// </summary>
public enum TeamSide
{
    None        = 0,
    Spectator   = 1,
    Terrorist   = 2,
    CounterTerrorist = 3
}
