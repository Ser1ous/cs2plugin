using System.Text;
using System.Text.Json;

namespace CS2MatchPlugin.Services;

/// <summary>
/// Fire-and-forget JSON POST notifier for match-lifecycle webhooks.
///
/// Two events are exposed:
///   • <see cref="PostRoundEnd"/> — fired after every live round, sends
///     <c>{"lobby":"&lt;matchId&gt;","round":&lt;n&gt;}</c>.
///   • <see cref="PostMapEnd"/>   — fired after every map win, sends
///     <c>{"lobby":"&lt;matchId&gt;"}</c>.
///
/// Both calls are non-blocking: they kick the HTTP request onto a
/// thread-pool task and return immediately. Errors are logged but never
/// thrown — a flaky webhook endpoint must NOT stall the game thread or
/// crash the plugin.
/// </summary>
public class WebhookNotifier
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    /// <summary>
    /// Fire-and-forget POST of <c>{"lobby":matchId,"round":round}</c> to
    /// the configured round-end URL. Empty/whitespace URL is a no-op.
    /// </summary>
    public void PostRoundEnd(string url, string matchId, int round)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        string payload = JsonSerializer.Serialize(new
        {
            lobby = matchId,
            round = round
        });
        _ = SendAsync(url, payload, $"round_end matchId={matchId} round={round}");
    }

    /// <summary>
    /// Fire-and-forget POST of <c>{"lobby":matchId}</c> to the configured
    /// map-start URL (sent when the knife round begins). Empty/whitespace URL is a no-op.
    /// </summary>
    public void PostMapStart(string url, string matchId)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        string payload = JsonSerializer.Serialize(new
        {
            lobby = matchId
        });
        _ = SendAsync(url, payload, $"map_start matchId={matchId}");
    }

    /// <summary>
    /// Fire-and-forget POST of <c>{"lobby":matchId}</c> to the configured
    /// map-end URL. Empty/whitespace URL is a no-op.
    /// </summary>
    public void PostMapEnd(string url, string matchId)
    {
        if (string.IsNullOrWhiteSpace(url)) return;

        string payload = JsonSerializer.Serialize(new
        {
            lobby = matchId
        });
        _ = SendAsync(url, payload, $"map_end matchId={matchId}");
    }

    private static async Task SendAsync(string url, string payload, string label)
    {
        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine(
                    $"[CS2Match] Webhook {label} → HTTP {(int)response.StatusCode} from {url}");
            }
            else
            {
                Console.WriteLine($"[CS2Match] Webhook {label} → OK ({url})");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CS2Match] Webhook {label} → failed: {ex.Message}");
        }
    }
}
