using System.Globalization;
using System.Text.Json;
using QuantDesk.Domain.Market;
using QuantDesk.Domain.Runtime;
using QuantDesk.Domain.Replay;
using QuantDesk.Runtime.State;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Runtime.Replay;

/// <summary>
/// Replays a recorded session through the runtime's own market-state machine.
///
/// Why this is the part of the decision path a market-data log can replay
/// ----------------------------------------------------------------------
/// <c>MarketStateStore.Apply</c> is real decision code, not a stand-in: the live pipeline calls it
/// on every entry and rejects with StaleMarketData when it refuses. Its answer is a function of the
/// quotes, trades and book updates that arrived and nothing else, which is exactly what the session
/// log contains -- so replaying it is a genuine reproduction rather than a re-run of a test decider.
///
/// What it does not cover, stated rather than implied
/// --------------------------------------------------
/// Strategy selection needs bars, the cost gate needs a fee schedule, the risk governor needs the
/// portfolio. None of those are in a market-data log, so none of them are replayed here. Widening
/// the gate means recording those inputs too, and claiming the whole pipeline replays on the
/// strength of this would be the kind of overstatement this codebase has already paid for once.
///
/// The trace
/// ---------
/// Each event yields the validation verdict and the decision-bearing fields of the resulting
/// snapshot -- the state version, the spread, the quality flags. Not the whole snapshot: including
/// a field nothing reads would make the trace hash sensitive to changes that cannot alter a
/// decision, and a gate that fails for reasons that do not matter gets switched off.
/// </summary>
public static class MarketStateReplay
{
    /// <summary>How many instrument slots a replayed session may address.</summary>
    public const int InstrumentCapacity = 32;

    /// <summary>
    /// A decision function over one recorded session, backed by a fresh state machine.
    ///
    /// Fresh per call, deliberately: the runner invokes this twice to prove determinism, and a
    /// state machine shared between the two passes would carry the first pass's state into the
    /// second and agree with itself for the wrong reason.
    /// </summary>
    public static Func<IRuntimeClock, ReplayEnvelope, (string Code, string Payload)?> Decider()
    {
        var store = new MarketStateStore(InstrumentCapacity);

        return (clock, envelope) =>
        {
            ArgumentNullException.ThrowIfNull(clock);
            ArgumentNullException.ThrowIfNull(envelope);

            int slot = SlotOf(envelope.Source);
            if (slot < 0 || slot >= InstrumentCapacity) return ("UNADDRESSABLE_SLOT", envelope.Source);

            using JsonDocument? payload = Parse(envelope.Payload);
            if (payload is null) return ("UNREADABLE_PAYLOAD", envelope.EventType);

            JsonElement body = payload.RootElement;
            long eventNs = envelope.EventUnixNanoseconds;
            long receiveTicks = clock.MonotonicTimestamp;

            ValidationResult applied = envelope.EventType switch
            {
                "quote" => store.Apply(new QuoteEvent(
                    Number(body, "EventId"), slot,
                    Real(body, "Bid"), Real(body, "Ask"),
                    Real(body, "BidSize"), Real(body, "AskSize"),
                    eventNs, receiveTicks, Number(body, "SourceSequence"))),

                "trade" => store.Apply(new TradeEvent(
                    Number(body, "EventId"), slot,
                    Real(body, "Price"), Real(body, "Size"),
                    eventNs, receiveTicks, Number(body, "SourceSequence"))),

                "orderbook" => store.Apply(new OrderBookEvent(
                    Number(body, "EventId"), slot,
                    Real(body, "BestBid"), Real(body, "BestAsk"),
                    Real(body, "BidDepth"), Real(body, "AskDepth"),
                    eventNs, receiveTicks, Number(body, "SourceSequence"))),

                _ => new ValidationResult(false, DataQuality.Invalid, "UNKNOWN_EVENT_TYPE"),
            };

            InstrumentSnapshot snapshot = store.Snapshot(slot);

            // StaleMarketData is the live pipeline's own refusal for exactly this verdict, so a
            // replay that reproduces it is reproducing a decision rather than a computation.
            string code = applied.IsValid ? "ACCEPTED" : applied.ReasonCode ?? "REFUSED";
            return (code, Describe(snapshot, applied));
        };
    }

    /// <summary>
    /// The decision-bearing fields of a snapshot, in a fixed order.
    ///
    /// Invariant culture and round-trip formatting, because the trace is hashed: a double formatted
    /// under a different culture would make the same session hash differently on another machine,
    /// which would read as non-determinism in the system rather than in its formatting.
    /// </summary>
    private static string Describe(in InstrumentSnapshot snapshot, in ValidationResult applied) =>
        string.Join('|',
            snapshot.StateVersion.ToString(CultureInfo.InvariantCulture),
            snapshot.Bid.ToString("R", CultureInfo.InvariantCulture),
            snapshot.Ask.ToString("R", CultureInfo.InvariantCulture),
            snapshot.Mid.ToString("R", CultureInfo.InvariantCulture),
            snapshot.RelativeSpread.ToString("R", CultureInfo.InvariantCulture),
            snapshot.QuoteQuality.ToString(),
            snapshot.TradeQuality.ToString(),
            snapshot.OrderBookQuality.ToString(),
            applied.Quality.ToString());

    /// <summary>The instrument slot the recorder encoded in the source name.</summary>
    private static int SlotOf(string source)
    {
        int separator = source.LastIndexOf(':');
        return separator >= 0
            && int.TryParse(source[(separator + 1)..], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int slot)
            ? slot
            : -1;
    }

    private static JsonDocument? Parse(byte[] payload)
    {
        try
        {
            return JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long Number(JsonElement body, string name) =>
        body.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long parsed)
            ? parsed
            : 0L;

    private static double Real(JsonElement body, string name) =>
        body.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double parsed)
            ? parsed
            : double.NaN;
}
