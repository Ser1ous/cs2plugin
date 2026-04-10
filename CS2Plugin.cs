using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CS2MatchPlugin.Commands;
using CS2MatchPlugin.Config;
using CS2MatchPlugin.Events;
using CS2MatchPlugin.Managers;
using CS2MatchPlugin.Services;

namespace CS2MatchPlugin;

public class CS2Plugin : BasePlugin, IPluginConfig<PluginConfig>
{
    public override string ModuleName    => "CS2 Match Plugin";
    public override string ModuleAuthor  => "cs2plugin";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleDescription =>
        "Competitive match management: URL config, knife round, ready system, pause, MySQL logging, AIM mode";

    public PluginConfig Config { get; set; } = new();

    // Services
    private DatabaseService  _db         = null!;
    private ConfigDownloader _downloader = null!;
    private MapChanger       _mapChanger = null!;
    private CfgExecutor      _cfgExecutor = null!;

    // Managers
    private ReadyManager           _readyManager  = null!;
    private PauseManager           _pauseManager  = null!;
    private KnifeManager           _knifeManager  = null!;
    private AimManager             _aimManager    = null!;
    private TeamEnforcementManager _enforcement   = null!;
    private MatchManager           _matchManager  = null!;

    // Commands / Events
    private AdminCommands      _adminCommands  = null!;
    private PlayerCommands     _playerCommands = null!;
    private PluginEventHandler _eventHandler   = null!;

    public void OnConfigParsed(PluginConfig config)
    {
        Config = config;
        if (string.IsNullOrWhiteSpace(config.MySqlPassword))
            Console.WriteLine("[CS2Match] Warning: MySqlPassword is empty in plugin config.");
        if (string.IsNullOrWhiteSpace(config.MySqlUser))
            Console.WriteLine("[CS2Match] Warning: MySqlUser is empty in plugin config.");
    }

    public override void Load(bool hotReload)
    {
        // --- Services ---
        _db = new DatabaseService();
        _db.Configure(Config);

        _downloader  = new ConfigDownloader();
        _mapChanger  = new MapChanger(this);
        _cfgExecutor = new CfgExecutor();

        // --- Managers ---
        _readyManager = new ReadyManager();
        _pauseManager = new PauseManager();
        _knifeManager = new KnifeManager();

        _aimManager = new AimManager(_mapChanger, _cfgExecutor);
        _aimManager.Configure(Config);

        _enforcement = new TeamEnforcementManager(this);

        _matchManager = new MatchManager(
            _downloader, _mapChanger, _cfgExecutor,
            _readyManager, _pauseManager, _knifeManager,
            _aimManager, _enforcement, _db, Config, this);

        // --- Commands & event handler ---
        _adminCommands  = new AdminCommands(_matchManager, _aimManager);
        _playerCommands = new PlayerCommands(_matchManager);
        _eventHandler   = new PluginEventHandler(_matchManager, _playerCommands, _db);

        // Admin console/chat commands
        AddCommand("ser_plug_load_url", "Load match config from URL",   _adminCommands.OnLoadUrlCommand);
        AddCommand("ser_plug_aim_mode", "Switch server to AIM mode",    _adminCommands.OnAimModeCommand);
        AddCommand("ser_plug_abort",    "Abort current match",          _adminCommands.OnAbortMatchCommand);
        AddCommand("ser_plug_status",   "Show match/server status",     _adminCommands.OnMatchStatusCommand);

        // Game events
        RegisterEventHandler<EventRoundStart>         (_eventHandler.OnRoundStart);
        RegisterEventHandler<EventRoundEnd>           (_eventHandler.OnRoundEnd);
        RegisterEventHandler<EventRoundFreezeEnd>     (_eventHandler.OnRoundFreezeEnd);
        RegisterEventHandler<EventPlayerDeath>        (_eventHandler.OnPlayerDeath);
        RegisterEventHandler<EventPlayerHurt>         (_eventHandler.OnPlayerHurt);
        RegisterEventHandler<EventPlayerBlind>        (_eventHandler.OnPlayerBlind);
        RegisterEventHandler<EventGrenadeThrown>      (_eventHandler.OnGrenadeThrown);
        RegisterEventHandler<EventBombPlanted>        (_eventHandler.OnBombPlanted);
        RegisterEventHandler<EventBombDefused>        (_eventHandler.OnBombDefused);
        RegisterEventHandler<EventBombBegindefuse>    (_eventHandler.OnBombBeginDefuse);
        RegisterEventHandler<EventBombExploded>       (_eventHandler.OnBombExploded);
        RegisterEventHandler<EventWeaponFire>         (_eventHandler.OnWeaponFire);
        RegisterEventHandler<EventPlayerSpawn>        (_eventHandler.OnPlayerSpawn);
        RegisterEventHandler<EventPlayerTeam>         (_eventHandler.OnPlayerChangeTeam);
        RegisterEventHandler<EventCsWinPanelMatch>    (_eventHandler.OnCsWinPanelMatch);
        RegisterEventHandler<EventPlayerConnectFull>  (_eventHandler.OnPlayerConnectFull);
        RegisterEventHandler<EventOtherDeath>         (_eventHandler.OnOtherDeath);
        RegisterEventHandler<EventRoundMvp>           (_eventHandler.OnRoundMvp);

        // Map lifecycle
        RegisterListener<Listeners.OnMapStart>(_eventHandler.OnMapStart);

        // Chat commands via say/say_team listeners (handles both ! and . prefixes)
        AddCommandListener("say",      _eventHandler.OnPlayerSay);
        AddCommandListener("say_team", _eventHandler.OnPlayerSayTeam);

        // Team enforcement — intercepts the M-menu "jointeam" command
        AddCommandListener("jointeam", _eventHandler.OnJoinTeam);

        // Keep server awake at all times so the first player connection never
        // hits a hibernating server (causes NETWORK_DISCONNECT_CREATE_SERVER_FAILED)
        Server.ExecuteCommand("sv_hibernate_when_empty 0");

        // Initialize DB tables
        _ = _db.InitializeTablesAsync();

        // If hot-reloaded, handle the current map immediately
        if (hotReload)
        {
            string currentMap = Server.MapName;
            _eventHandler.OnMapStart(currentMap);
        }

        Console.WriteLine("[CS2Match] Plugin loaded successfully.");
    }

    public override void Unload(bool hotReload)
    {
        Console.WriteLine("[CS2Match] Plugin unloading.");
    }
}
