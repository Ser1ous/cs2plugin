using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace CS2MatchPlugin.Services;

/// <summary>
/// Schedules cancellable, workshop-aware map changes.
///
/// History note: this class used to also flip game_type/game_mode to
/// switch CS2 presets (Casual ↔ Competitive). That logic was removed
/// — the plugin now leaves game_type/game_mode at whatever the server
/// boots with, and only handles the map change itself. The class kept
/// its old name to avoid touching every callsite; conceptually it is
/// now just a "delayed map change scheduler with cancellation".
///
/// What it still does:
///   • Workshop-aware dispatch — numeric (ulong) names go through
///     <c>host_workshop_map</c>, others via <c>changelevel</c>.
///   • Non-blocking AddTimer-based delay (default 1.0s) so callers can
///     do their state teardown synchronously and let the engine
///     breathe before the level transition.
///   • Cancellation via a monotonic sequence number — rapid repeats
///     (admin spamming load_url) cancel any prior pending map change
///     so map reloads never stack.
///   • Post-reload unpause cvars (mp_warmup_pausetimer 0,
///     mp_unpause_match) so the next map never comes up paused.
/// </summary>
public class GameModeSwitcher
{
    /// <summary>
    /// Monotonically-incrementing sequence number for pending delayed
    /// map-change operations. Every call to <see cref="MapChangeDelayed"/>
    /// bumps this. When a scheduled AddTimer callback fires, it compares
    /// its captured sequence to the current value — if they differ, a
    /// newer request has superseded it and the callback becomes a no-op.
    /// </summary>
    private int _pendingSequence = 0;

    /// <summary>
    /// BasePlugin reference required to schedule the delayed map-change
    /// callback via AddTimer.
    /// </summary>
    private readonly BasePlugin? _plugin;

    public GameModeSwitcher() { }
    public GameModeSwitcher(BasePlugin plugin) { _plugin = plugin; }

    /// <summary>
    /// Sequential "stop trackers → wait → change map" with cancellation
    /// of any previously-scheduled call. Used by both the
    /// ser_plug_load_url path and the return-to-AIM-after-match path.
    ///
    /// Steps:
    ///   1. Log "<label>. Waiting Ns before map change..."
    ///   2. Run the caller-supplied <paramref name="onBeforeDelay"/>
    ///      callback synchronously on the main thread (used for active-
    ///      match teardown that has to land before the engine swap).
    ///   3. Bump _pendingSequence (cancels any still-pending callback
    ///      from a prior call).
    ///   4. AddTimer(delaySeconds, …) schedules the actual map change.
    ///   5. Inside the callback: cancellation check → DispatchMapChange
    ///      → mp_warmup_pausetimer 0 + mp_unpause_match.
    /// </summary>
    public void MapChangeDelayed(
        string mapName,
        string label,
        float delaySeconds = 1.0f,
        Action? onBeforeDelay = null)
    {
        if (_plugin == null)
        {
            Console.WriteLine(
                "[CS2Match] GameModeSwitcher: _plugin is null — cannot schedule " +
                "delayed map change. Check DI wiring in CS2Plugin.cs");
            return;
        }

        Console.WriteLine(
            $"[CS2Match] {label}. Waiting {delaySeconds:0.0}s before map change to '{mapName}'...");

        // Step 2: synchronous pre-delay hook (state teardown).
        try { onBeforeDelay?.Invoke(); }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[CS2Match] MapChangeDelayed: onBeforeDelay threw: {ex.Message}");
        }

        // Step 3: cancel any in-flight prior call.
        int mySequence = System.Threading.Interlocked.Increment(ref _pendingSequence);

        Console.WriteLine(
            $"[CS2Match] Scheduled map change to '{mapName}' in {delaySeconds:0.0}s (seq={mySequence})");

        // Steps 4-5: delayed map change.
        _plugin.AddTimer(delaySeconds, () =>
        {
            int currentSeq = System.Threading.Volatile.Read(ref _pendingSequence);
            if (currentSeq != mySequence)
            {
                Console.WriteLine(
                    $"[CS2Match] Map-change callback seq={mySequence} " +
                    $"cancelled (current={currentSeq}) — superseded by newer request");
                return;
            }

            DispatchMapChange(mapName);

            // Belt-and-braces unpause cvars so the reloaded map never
            // comes up with leftover pause state.
            Server.ExecuteCommand("mp_warmup_pausetimer 0");
            Server.ExecuteCommand("mp_unpause_match");
        });
    }

    /// <summary>
    /// Workshop-aware map change dispatcher. Numeric (ulong-parseable)
    /// names go through <c>host_workshop_map</c>; everything else uses
    /// <c>changelevel</c>.
    /// </summary>
    public void DispatchMapChange(string mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName))
        {
            Console.WriteLine("[CS2Match] DispatchMapChange: empty map name, aborting");
            return;
        }

        string trimmed = mapName.Trim();

        if (IsWorkshopId(trimmed))
        {
            Console.WriteLine($"[CS2Match] DispatchMapChange: host_workshop_map {trimmed}");
            Server.ExecuteCommand($"host_workshop_map {trimmed}");
        }
        else
        {
            Console.WriteLine($"[CS2Match] DispatchMapChange: changelevel {trimmed}");
            Server.ExecuteCommand($"changelevel {trimmed}");
        }
    }

    private static bool IsWorkshopId(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (char c in value)
        {
            if (c < '0' || c > '9') return false;
        }
        return ulong.TryParse(value, out _);
    }
}
