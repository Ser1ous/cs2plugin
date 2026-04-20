# CS2 Match Plugin — Claude Context

## What this project is

A [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) plugin for CS2 written in C# (.NET 8). It manages competitive matches: loads a match config from a JSON URL, runs warmup → knife round → live match → multi-map series, and logs everything to MySQL.

## Build

```bash
dotnet publish cs2plugin.csproj -c Release -o ./publish
```

Only two output files matter: `CS2MatchPlugin.dll` and `MySqlConnector.dll`.

## Project structure

```
CS2Plugin.cs              — Entry point, wires everything together, registers all events/commands
Commands/
  AdminCommands.cs        — ser_plug_* server commands (require @css/root or @css/admin)
  PlayerCommands.cs       — !ready, .ct, .t, .pause, .unpause (chat commands)
Config/
  PluginConfig.cs         — MySQL creds, cfg file names, MinPlayersToStart
  MatchConfig.cs          — Deserialized match JSON (matchid, teams, maplist, map_sides, cvars)
Events/
  EventHandler.cs         — Routes all CS2 game events to MatchManager
Managers/
  MatchManager.cs         — Core orchestrator: entire match lifecycle, state transitions, stats
  ReadyManager.cs         — Tracks !ready per player, fires OnAllReady
  PauseManager.cs         — Technical pause voting (.pause / .unpause)
  KnifeManager.cs         — Detects knife round winner by team score
  AimManager.cs           — AIM mode (practice map) when no match is active
  TeamEnforcementManager.cs — Locks players to assigned teams during Live
Services/
  DatabaseService.cs      — Async MySQL logging (kills, chat, rounds, match events)
  ConfigDownloader.cs     — Downloads match JSON from URL
  MapChanger.cs           — changelevel wrapper
  CfgExecutor.cs          — exec <name>.cfg wrapper
  GameModeSwitcher.cs     — game_type / game_mode cvars
  WebhookNotifier.cs      — HTTP webhook callbacks
State/
  MatchContext.cs         — Central state: match config, per-player stats, scores, round tracking
  MatchState.cs           — Enum: None | Aim | Warmup | Knife | SidePick | Live | Paused
  TeamSide.cs             — Enum: None | Spectator | Terrorist | CounterTerrorist
configs/                  — warmup.cfg, knife.cfg, competitive.cfg, aim.cfg (exec'd at phase transitions)
```

## Match flow

```
ser_plug_load_url <url>
  → map changes
  → WARMUP  (warmup.cfg, players type !ready)
  → KNIFE ROUND  (knife.cfg, 1 round, winner picks .ct or .t)  — skipped if map_sides != "knife"
  → LIVE  (competitive.cfg + cvars from JSON)
  → next map in series (repeat warmup → knife → live)
  → AIM MODE
```

State is stored in `MatchContext.State` (type `MatchState`). Always check this before acting on events.

## Key patterns

### Checking game phase
```csharp
var ctx = _matchManager.Context;
if (ctx?.State == MatchState.Warmup) { ... }
if (ctx?.State != MatchState.Live) return HookResult.Continue;
```

### Setting player money
```csharp
player.InGameMoneyServices.Account = 16000;
Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
```
`SetStateChanged` is required — without it the client won't see the update.

### Reading player money
```csharp
int account = player.InGameMoneyServices?.Account ?? 0;
```

### Executing a cfg file
```csharp
_cfgExecutor.Execute("warmup"); // runs: exec warmup.cfg
```

### Scheduling a deferred action (server tick safe)
```csharp
AddTimer(0.1f, () => { ... });
```

## Event handler conventions

- All events go through `EventHandler.cs` → delegated to `MatchManager`.
- Most handlers start with a state guard: `if (ctx?.State != MatchState.Live) return HookResult.Continue;`
- Warmup-specific logic uses `ctx?.State == MatchState.Warmup`.
- `OnPlayerSpawn` handles both Warmup (sets $16k) and Live (team enforcement + spawn recording).

## Important: when to call SetStateChanged

Any direct write to a networked CS2 entity property requires `Utilities.SetStateChanged(entity, "ClassName", "m_propertyName")` to propagate to clients. Forgetting it means the server has the new value but clients do not.

## Config files (configs/)

| File | Executed when |
|---|---|
| `warmup.cfg` | Map load, before players ready |
| `knife.cfg` | Knife round start |
| `competitive.cfg` | Match goes live |
| `aim.cfg` | AIM mode entered |

These are plain CS2 cfg files — add any cvars freely.

## Match JSON (loaded via ser_plug_load_url)

Required fields: `matchid`, `maplist` (array).  
`map_sides` values per map: `"knife"`, `"team1_ct"`, `"team2_ct"`.  
`cvars` object: applied on top of competitive.cfg when match goes live.  
Players identified by SteamID64 string keys in `team1.players` / `team2.players`.

## MySQL

Tables created automatically on plugin load. Key tables:
- `match_events` — lifecycle events
- `kill_events` — one row per kill (Live state only)
- `round_events` — round start/end with score
- `chat_events` — all chat during active match

## AIM mode

Entered automatically on: plugin load (if no match), after series ends, after `ser_plug_abort`.  
Configured by `AimMapName` and `AimCfgName` in `plugin_config.json`.

## Admin commands

| Command | What it does |
|---|---|
| `ser_plug_load_url <url>` | Load match from JSON URL |
| `ser_plug_status` | Show current state/score/map |
| `ser_plug_abort` | Abort match, enter AIM mode |
| `ser_plug_aim_mode` | Force AIM mode |

## Player commands (chat, ! or . prefix)

`!ready` / `.ready` — warmup only  
`.ct` / `.t` — side pick after knife  
`.pause` / `.unpause` — live match  
