# CS2 Match Plugin

A CounterStrikeSharp plugin for CS2 competitive match management. Supports loading match configs from a URL, knife rounds, ready system, technical pauses, multi-map series, AIM mode, and full MySQL event logging.

---

## Features

- Load a match config from any HTTP URL (`ser_plug_load_url`)
- Warmup with `!ready` / `.ready` system — match starts only when all registered players are ready
- Knife round to determine side selection (winner types `.ct` or `.t`)
- Technical pause (`.pause`) with no timeout — both teams must `.unpause` to resume
- Multi-map series with automatic map rotation and halftime side swap
- AIM mode when no match is active — switches to a configured aim map automatically
- Predetermined sides support (`team1_ct`, `team2_ct`) to skip the knife round
- MySQL logging: kills (with weapon, headshot, position), chat, round events, match events
- All settings in config files — nothing hardcoded
- Tables created automatically on first load

---

## Requirements

### Server

| Requirement | Version |
|---|---|
| CS2 Dedicated Server | Latest |
| [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) | ≥ 1.0.305 |
| MySQL / MariaDB | ≥ 5.7 / 10.3 |
| .NET Runtime | 8.0 (bundled with CSS) |

### Build machine (to compile the plugin)

- [.NET SDK 8.0+](https://dotnet.microsoft.com/download)

---

## Building

Clone or download the repository, then run from the project directory:

```bash
dotnet publish cs2plugin.csproj -c Release -o ./publish
```

The compiled output will be in `./publish/`. You only need two files from it:

```
publish/
├── CS2MatchPlugin.dll   ← your plugin
└── MySqlConnector.dll   ← MySQL driver
```

---

## Deployment

### 1. Copy plugin files

Create the plugin folder on your server and copy the two DLLs:

```
game/csgo/addons/counterstrikesharp/plugins/CS2MatchPlugin/
├── CS2MatchPlugin.dll
└── MySqlConnector.dll
```

### 2. Copy server config files

Copy the four `.cfg` files from `configs/server_cfgs/` to your CS2 server cfg directory:

```
game/csgo/cfg/
├── warmup.cfg
├── knife.cfg
├── competitive.cfg
└── aim.cfg
```

You can edit these files freely — they are `exec`'d by the plugin at the appropriate phase transitions.

### 3. Create the MySQL database

Connect to your MySQL server and run:

```sql
CREATE DATABASE cs2_matches CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER 'cs2user'@'localhost' IDENTIFIED BY 'your_password';
GRANT ALL PRIVILEGES ON cs2_matches.* TO 'cs2user'@'localhost';
FLUSH PRIVILEGES;
```

> The plugin creates all required tables automatically on first load. You do not need to run any SQL schema manually.

### 4. Configure the plugin

On first load, CounterStrikeSharp generates the config file at:

```
game/csgo/addons/counterstrikesharp/configs/plugins/CS2MatchPlugin/plugin_config.json
```

Edit it with your settings:

```json
{
  "ConfigVersion": 1,
  "MySqlHost": "127.0.0.1",
  "MySqlPort": 3306,
  "MySqlDatabase": "cs2_matches",
  "MySqlUser": "cs2user",
  "MySqlPassword": "your_password",
  "AimMapName": "aim_map",
  "AimCfgName": "aim",
  "WarmupCfgName": "warmup",
  "KnifeCfgName": "knife",
  "CompetitiveCfgName": "competitive",
  "MinPlayersToStart": 0
}
```

| Field | Description |
|---|---|
| `MySqlHost` | MySQL server address |
| `MySqlPort` | MySQL port (default 3306) |
| `MySqlDatabase` | Database name |
| `MySqlUser` | MySQL username |
| `MySqlPassword` | MySQL password |
| `AimMapName` | Map name to load in AIM mode (e.g. `aim_map`, `aim_redline`) |
| `AimCfgName` | Name of the cfg file for AIM mode (without `.cfg`) |
| `WarmupCfgName` | Cfg file executed during warmup |
| `KnifeCfgName` | Cfg file executed for the knife round |
| `CompetitiveCfgName` | Cfg file executed when the live match starts |
| `MinPlayersToStart` | Minimum players to require before allowing match load (0 = no limit) |

Restart the server or reload the plugin after editing this file.

---

## Match JSON format

The match config is fetched from a URL you provide. It must be valid JSON in this format:

```json
{
  "matchid": "45",
  "num_maps": 1,
  "mode": "competitive",
  "maplist": ["de_anubis"],
  "map_sides": ["knife"],
  "clinch_series": true,
  "players_per_team": 5,
  "team1": {
    "name": "Team 1",
    "players": {
      "76561198069541010": "Ser1ous",
      "76561199107157256": "m0wZL",
      "76561198103768399": "Bolik|ua",
      "76561198215681115": "Sky.Bo",
      "76561198085464894": "Lioshin"
    }
  },
  "team2": {
    "name": "Team 2",
    "players": {
      "76561198094744447": "Panama",
      "76561198077308377": "InFoS",
      "76561198060887126": "Лопата",
      "76561198429470295": "Drama Queen",
      "76561199809410833": "ssawket"
    }
  },
  "cvars": {
    "mp_overtime_maxrounds": "6",
    "mp_freezetime": "18"
  }
}
```

### Field reference

| Field | Type | Description |
|---|---|---|
| `matchid` | string | Unique match identifier, used as key in all DB tables |
| `num_maps` | int | Total maps in the series (1, 2, 3, …) |
| `mode` | string | Reserved for future use (`competitive`) |
| `maplist` | string[] | Ordered list of maps to play |
| `map_sides` | string[] | Per-map side assignment: `"knife"`, `"team1_ct"`, or `"team2_ct"` |
| `clinch_series` | bool | If true, series ends as soon as one team wins `ceil(num_maps/2)` maps |
| `players_per_team` | int | Expected players per team (used for ready count if player lists are empty) |
| `team1.name` | string | Display name for team 1 |
| `team1.players` | object | Map of SteamID64 (string) → in-game alias |
| `team2.name` | string | Display name for team 2 |
| `team2.players` | object | Map of SteamID64 (string) → in-game alias |
| `cvars` | object | Extra console variables applied when the match goes live (override competitive.cfg) |

### `map_sides` values

| Value | Meaning |
|---|---|
| `"knife"` | Knife round played, winner picks CT or T |
| `"team1_ct"` | Team 1 starts as CT, no knife round |
| `"team2_ct"` | Team 2 starts as CT, no knife round |

---

## Server commands

All `ser_plug_*` commands require admin permissions (`@css/root` or `@css/admin`).  
They can be run from server console or in-game by an admin.

| Command | Description |
|---|---|
| `ser_plug_load_url <url>` | Download match config from URL and start the match flow |
| `ser_plug_status` | Show current match state, score, and map |
| `ser_plug_abort` | Abort the active match and return to AIM mode |
| `ser_plug_aim_mode` | Force switch to AIM mode (also aborts any active match) |

### Example

```
ser_plug_load_url https://yourserver.com/matches/45.json
```

---

## Player commands (in-game chat)

These are typed in chat. Both `!` and `.` prefixes are supported where listed.

| Command | Phase | Description |
|---|---|---|
| `!ready` or `.ready` | Warmup | Mark yourself as ready |
| `.ct` | Side pick | Knife winner picks CT side |
| `.t` | Side pick | Knife winner picks T side |
| `.pause` | Live | Call a technical pause (no timeout) |
| `.unpause` | Paused | Vote to resume — both teams must type this |

> Players not listed in the match config (by SteamID64) cannot use `!ready` and are treated as spectators.

---

## Match flow

```
ser_plug_load_url <url>
        │
        ▼
   Map changes
        │
        ▼
   WARMUP ──── all players type !ready ────▶ (if map_sides = "knife")
        │                                              │
        │                                    KNIFE ROUND (1 round)
        │                                              │
        │                                    Winner types .ct or .t
        │                                              │
        └──────────────────────────────────────────────▼
                                              LIVE MATCH
                                             (24 rounds + OT)
                                                  │
                                           Map ends → next map
                                           (warmup → knife → live)
                                                  │
                                           Series complete
                                                  │
                                              AIM MODE
```

---

## AIM mode

When no match is active (on plugin load, after a series ends, or after `ser_plug_aim_mode`), the server automatically:

1. Changes to the map specified in `AimMapName`
2. Executes `aim.cfg`

Edit `aim.cfg` to configure respawn, buy settings, round time, etc. for your aim map.

---

## MySQL tables

Tables are created automatically with `CREATE TABLE IF NOT EXISTS` on plugin load.

### `match_events`
General match lifecycle events (loaded, live, paused, series end, etc.)

| Column | Type | Description |
|---|---|---|
| `id` | BIGINT | Auto-increment PK |
| `match_id` | VARCHAR(64) | Match identifier |
| `event_type` | VARCHAR(64) | Event name (e.g. `match_live`, `knife_winner`) |
| `data` | JSON | Optional event payload |
| `created_at` | DATETIME | Timestamp |

### `kill_events`
One row per kill during a live match.

| Column | Type | Description |
|---|---|---|
| `id` | BIGINT | Auto-increment PK |
| `match_id` | VARCHAR(64) | Match identifier |
| `round` | INT | Round number |
| `attacker_steamid` | BIGINT UNSIGNED | Attacker SteamID64 (NULL = world/suicide) |
| `attacker_name` | VARCHAR(128) | Attacker name |
| `attacker_team` | TINYINT | Attacker team number (2=T, 3=CT) |
| `victim_steamid` | BIGINT UNSIGNED | Victim SteamID64 |
| `victim_name` | VARCHAR(128) | Victim name |
| `victim_team` | TINYINT | Victim team number |
| `weapon` | VARCHAR(64) | Weapon classname |
| `headshot` | TINYINT(1) | 1 if headshot |
| `attacker_x/y/z` | FLOAT | Attacker world position |
| `victim_x/y/z` | FLOAT | Victim world position |
| `created_at` | DATETIME | Timestamp |

### `chat_events`
All chat messages sent during an active match.

| Column | Type | Description |
|---|---|---|
| `id` | BIGINT | Auto-increment PK |
| `match_id` | VARCHAR(64) | Match identifier |
| `round` | INT | Round number |
| `steamid` | BIGINT UNSIGNED | Sender SteamID64 |
| `player_name` | VARCHAR(128) | Sender name |
| `message` | TEXT | Message content |
| `team_only` | TINYINT(1) | 1 if team chat |
| `created_at` | DATETIME | Timestamp |

### `round_events`
Round start / end events with running score.

| Column | Type | Description |
|---|---|---|
| `id` | BIGINT | Auto-increment PK |
| `match_id` | VARCHAR(64) | Match identifier |
| `round` | INT | Round number |
| `event_type` | VARCHAR(64) | `round_start` or `round_end` |
| `team1_score` | INT | Team 1 score at this moment |
| `team2_score` | INT | Team 2 score at this moment |
| `winner_team` | TINYINT | Winning team number (round_end only) |
| `created_at` | DATETIME | Timestamp |

---

## Server cfg files

All four files are executed with `exec <name>.cfg` at the appropriate time.  
Place them in `game/csgo/cfg/`. You can freely add or change any cvars in these files.

| File | When executed |
|---|---|
| `warmup.cfg` | On map load, before players are ready |
| `knife.cfg` | At knife round start |
| `competitive.cfg` | When the live match begins |
| `aim.cfg` | When server enters AIM mode |

---

## Troubleshooting

**Plugin doesn't load**  
Check `addons/counterstrikesharp/logs/` for errors. Make sure both `CS2MatchPlugin.dll` and `MySqlConnector.dll` are in the plugin folder.

**MySQL connection fails**  
- Verify credentials in `plugin_config.json`
- Make sure the MySQL user has access from the server's IP (use `'cs2user'@'%'` for remote access)
- Check that port 3306 is not firewalled

**`ser_plug_load_url` returns an error**  
- Confirm the URL is reachable from the server (`curl <url>` in the server shell)
- Validate the JSON format — `matchid` and at least one map in `maplist` are required

**Knife round winner is wrong team**  
The plugin detects which config team is on which side by inspecting connected players' `TeamNum`. Make sure players are on the correct team in-game before the knife round ends.

**Players can't type `!ready`**  
Their SteamID64 must be listed in `team1.players` or `team2.players` in the match config. SteamID64 values must be strings (quoted) in the JSON.

**AIM map keeps looping**  
If the aim map name in `plugin_config.json` doesn't match the actual map file name exactly (case-sensitive on Linux), the plugin will keep trying to switch to it. Verify the map filename with `ls game/csgo/maps/`.


 dotnet publish cs2plugin.csproj -c Release -o ./release