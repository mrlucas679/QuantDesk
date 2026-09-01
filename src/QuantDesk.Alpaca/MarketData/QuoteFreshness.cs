namespace QuantDesk.Alpaca.MarketData;

/// <summary>
/// Decides whether a venue timestamp is recent enough to price from.
///
/// The subtlety is the future side. A quote stamped after the caller's <c>asOf</c> was previously refused
/// outright, which reads as prudent and is not: the venue stamps quotes to the nanosecond from its own
/// clock, the caller stamps <c>asOf</c> from a different one, and the two are never exactly aligned. Under
/// a strictly-in-the-past rule, a local clock trailing the venue's by a few milliseconds would mark every
/// healthy quote stale and silently refuse every spread — a whole lane disabled by ordinary clock drift,
/// with nothing in the logs but "stale".
///
/// So a small skew is tolerated and a large one is not. A quote seconds ahead is not drift; it is a data
/// error, and pricing from it would mean trading on a quote that has not happened yet.
/// </summary>
internal static class QuoteFreshness
{
    /// <summary>
    /// How far ahead of the caller's clock a venue timestamp may sit and still be believed. Sized for
    /// ordinary NTP drift between two synchronised hosts, not for a meaningfully wrong clock.
    /// </summary>
    public static readonly TimeSpan MaximumClockSkew = TimeSpan.FromSeconds(2);

    /// <summary>
    /// True when <paramref name="timestamp"/> is no older than <paramref name="maximumAge"/> and no
    /// further ahead of <paramref name="asOf"/> than <see cref="MaximumClockSkew"/>.
    /// </summary>
    public static bool IsFresh(DateTimeOffset timestamp, DateTimeOffset asOf, TimeSpan maximumAge)
    {
        TimeSpan age = asOf - timestamp;
        return age <= maximumAge && age >= -MaximumClockSkew;
    }
}
