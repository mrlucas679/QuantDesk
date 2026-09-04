using QuantDesk.Runtime.Time;

namespace QuantDesk.Runtime.Ingestion;

/// <summary>What one stream's connection history looks like.</summary>
/// <param name="Name">Which stream.</param>
/// <param name="Connects">How many times it has come up.</param>
/// <param name="Disconnects">How many times it has gone down.</param>
/// <param name="Connected">Whether it is up now.</param>
/// <param name="LastChangeAt">When it last changed state.</param>
/// <param name="ReconnectsInWindow">Disconnects within the recent window, which is what a leak looks like.</param>
public sealed record StreamConnectionSummary(
    string Name,
    long Connects,
    long Disconnects,
    bool Connected,
    DateTimeOffset? LastChangeAt,
    int ReconnectsInWindow);

/// <summary>
/// Counts stream connections and disconnections, so a reconnect loop is a number rather than a
/// pattern somebody notices in the logs.
///
/// Why this is a release gate and not just telemetry
/// -------------------------------------------------
/// Gate R12 requires "no reconnect leak", and until now nothing in this system counted a reconnect
/// at all -- the attestation reported the property false because there was no way to answer it.
/// A socket that drops and redials every few seconds keeps every health check green: each
/// individual connection succeeds, data flows in bursts, and readiness flickers back to healthy
/// between drops. What it destroys is the market state, because this venue publishes no sequence
/// number and every reconnect silently loses an unknown number of updates.
///
/// So the measure that matters is not whether the stream is up. It is how often it has had to come
/// back up recently.
/// </summary>
public sealed class StreamConnectionTracker(IRuntimeClock clock)
{
    /// <summary>
    /// How far back a reconnect still counts toward the leak measure.
    ///
    /// Long enough that a slow flap is visible, short enough that yesterday's network blip does not
    /// hold the gate closed forever.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Reconnects within the window that still count as healthy operation.
    ///
    /// A venue drops a socket occasionally and that is ordinary; several drops in half an hour is a
    /// stream that is not staying up, and every one of them cost an unknown slice of the book.
    /// </summary>
    public const int MaximumReconnectsInWindow = 3;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, StreamState> _streams = new(StringComparer.Ordinal);

    /// <summary>Records a connection state change for one stream.</summary>
    public void Record(string name, bool connected, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        lock (_gate)
        {
            if (!_streams.TryGetValue(name, out StreamState? state))
            {
                state = new StreamState();
                _streams[name] = state;
            }

            // Only transitions count. A stream reporting "still connected" on a timer would
            // otherwise inflate the connect count and hide a genuine flap behind the noise.
            if (state.LastChangeAt is not null && state.Connected == connected) return;

            state.Connected = connected;
            state.LastChangeAt = at;

            if (connected)
            {
                state.Connects++;
            }
            else
            {
                state.Disconnects++;
                state.DisconnectedAt.Add(at);
            }
        }
    }

    /// <summary>Every stream's history, newest state included.</summary>
    public IReadOnlyList<StreamConnectionSummary> Summarise()
    {
        DateTimeOffset cutoff = clock.UtcNow - Window;

        lock (_gate)
        {
            return
            [
                .. _streams.Select(entry => new StreamConnectionSummary(
                    entry.Key,
                    entry.Value.Connects,
                    entry.Value.Disconnects,
                    entry.Value.Connected,
                    entry.Value.LastChangeAt,
                    entry.Value.DisconnectedAt.Count(at => at >= cutoff)))
                    .OrderBy(summary => summary.Name, StringComparer.Ordinal),
            ];
        }
    }

    /// <summary>
    /// True when no stream has reconnected more than the window allows.
    ///
    /// A system that has never connected anything is not leaking, and says so -- the gate that
    /// consumes this pairs it with the readiness flags that require the streams to be up, so an
    /// idle process cannot pass R12 on the strength of having done nothing.
    /// </summary>
    public bool NoReconnectLeak() =>
        Summarise().All(summary => summary.ReconnectsInWindow <= MaximumReconnectsInWindow);

    private sealed class StreamState
    {
        public long Connects;
        public long Disconnects;
        public bool Connected;
        public DateTimeOffset? LastChangeAt;
        public List<DateTimeOffset> DisconnectedAt { get; } = [];
    }
}
