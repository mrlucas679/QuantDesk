using QuantDesk.Domain.Options;
using QuantDesk.Domain.Runtime;

namespace QuantDesk.Alpaca.MarketData;

/// <summary>One stage of the preflight, and what the venue said when it ran.</summary>
public sealed record OptionPreflightStep(string Name, OptionPreflightOutcome Outcome, string Detail);

/// <summary>
/// Whether a stage established what it set out to, could not run, or was refused by the venue.
/// </summary>
public enum OptionPreflightOutcome
{
    /// <summary>The venue answered and the answer is usable.</summary>
    Passed,

    /// <summary>The venue answered and the answer is unusable. Named so it can be acted on.</summary>
    Failed,

    /// <summary>Not attempted, because an earlier stage did not produce what it needed.</summary>
    Skipped
}

/// <summary>The whole preflight. Passes only when every stage that ran passed.</summary>
public sealed record OptionPreflightReport(string Underlying, IReadOnlyList<OptionPreflightStep> Steps)
{
    public bool Passed => Steps.All(step => step.Outcome == OptionPreflightOutcome.Passed);
}

/// <summary>
/// Exercises every option data path against the live venue, read-only, and reports what each one
/// actually returned.
///
/// This exists because of a specific gap: the option clients had never been run against Alpaca, only
/// against payload shapes taken from its documentation. Shapes are now covered by tests; behaviour is
/// not, and cannot be until credentials exist. What can be prepared in advance is the *first contact*
/// — so that it produces a report rather than a stack trace.
///
/// Two properties matter more than the checks themselves:
///
/// <list type="bullet">
/// <item>It places no orders and cancels nothing. Every call is a read.</item>
/// <item>A stage that fails does not stop the ones that do not depend on it. The point is to learn
/// everything wrong in one run — discovering that contracts resolve but quotes are unentitled is a
/// different situation from discovering neither works, and finding out one call at a time wastes the
/// scarcest thing here, which is attempts against a venue nobody has reached yet.</item>
/// </list>
///
/// Exclusions are reported rather than treated as failure. A chain containing adjusted contracts is
/// healthy; a chain that is <em>entirely</em> excluded is not, and the difference is only visible if
/// the count is printed.
/// </summary>
public sealed class OptionDataPreflight(
    AlpacaOptionContractClient contracts,
    AlpacaLatestOptionQuoteClient quotes,
    AlpacaOptionRiskSnapshotClient snapshots,
    AlpacaHistoricalOptionBarClient bars)
{
    /// <summary>How many discovered contracts to price. Enough to be representative, small enough to
    /// stay within one request on every endpoint.</summary>
    private const int SampleSize = 4;

    /// <summary>How old a quote may be and still price a spread.</summary>
    private static readonly TimeSpan MaximumQuoteAge = TimeSpan.FromMinutes(15);

    public async Task<OptionPreflightReport> RunAsync(
        string underlying,
        DateOnly expirationStart,
        DateOnly expirationEnd,
        DateTimeOffset asOf,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(underlying);

        var steps = new List<OptionPreflightStep>();
        IReadOnlyList<AlpacaOptionContract> sample = [];

        try
        {
            OptionContractQuery discovered = await contracts.ListAsync(
                underlying, expirationStart, expirationEnd, "active", cancellationToken);
            sample = [.. discovered.Contracts.Where(contract => contract.Tradable).Take(SampleSize)];
            steps.Add(DescribeDiscovery(discovered, sample));
        }
        catch (Exception exception) when (IsVenueFailure(exception))
        {
            steps.Add(new OptionPreflightStep(
                "contract discovery", OptionPreflightOutcome.Failed, Describe(exception)));
        }

        if (sample.Count == 0)
        {
            steps.Add(Skipped("latest quotes"));
            steps.Add(Skipped("greeks and implied volatility"));
            steps.Add(Skipped("historical bars"));
            return new OptionPreflightReport(underlying, steps);
        }

        Dictionary<string, int> slots = sample
            .Select((contract, index) => (contract.Symbol, index))
            .ToDictionary(item => item.Symbol, item => item.index, StringComparer.Ordinal);

        steps.Add(await RunStepAsync("latest quotes", async () =>
        {
            IReadOnlyDictionary<int, OptionQuoteSnapshot> priced = await quotes.GetQuotesAsync(
                slots, asOf, MaximumQuoteAge, cancellationToken);
            int healthy = priced.Values.Count(quote => quote.Quality == DataQuality.Healthy);
            if (healthy > 0)
            {
                return (true, $"{healthy}/{priced.Count} usable; tightest relative spread " +
                    $"{priced.Values.Where(quote => quote.Quality == DataQuality.Healthy)
                        .Min(quote => quote.RelativeSpread):P2}");
            }

            // A quote is what makes a defined-risk debit computable, so none usable is a failure even
            // when the venue answered successfully. But "the feed is dead" and "the market is shut"
            // both arrive as zero usable quotes, and they call for completely different responses —
            // so say which one this is, using the age of what actually came back.
            int quoted = priced.Values.Count(quote => quote.EventNs > 0);
            return (false, quoted == 0
                ? $"0/{priced.Count} usable; the venue returned no quote for any sampled contract"
                : $"0/{priced.Count} usable; {quoted} quoted but all older than the " +
                  $"{MaximumQuoteAge.TotalMinutes:N0}-minute limit. Newest is {DescribeAge(priced, asOf)}. " +
                  $"{SessionNote(asOf)}");
        }));

        steps.Add(await RunStepAsync("greeks and implied volatility", async () =>
        {
            IReadOnlyDictionary<int, OptionRiskSnapshot> risk = await snapshots.GetSnapshotsAsync(
                slots, asOf, MaximumQuoteAge, cancellationToken);
            int healthy = risk.Values.Count(item => item.Quality == DataQuality.Healthy);
            return (healthy > 0, healthy > 0
                ? $"{healthy}/{risk.Count} usable"
                : $"0/{risk.Count} usable; the venue returned no greeks block, or its quote was stale. " +
                  SessionNote(asOf));
        }));

        steps.Add(await RunStepAsync("historical bars", async () =>
        {
            OptionBarQuery history = await bars.GetBarsAsync(
                [.. slots.Keys], asOf.AddDays(-7), asOf, "1Day", cancellationToken);
            int total = history.Bars.Values.Sum(series => series.Count);
            // Zero bars is reportable but not a failure: a freshly listed contract legitimately has none.
            return (true, $"{total} bars across {history.Bars.Count} contracts over 7 days");
        }));

        return new OptionPreflightReport(underlying, steps);
    }

    /// <summary>How stale the freshest returned quote is, in units a person reads at a glance.</summary>
    private static string DescribeAge(
        IReadOnlyDictionary<int, OptionQuoteSnapshot> priced, DateTimeOffset asOf)
    {
        long newestNs = priced.Values.Where(quote => quote.EventNs > 0).Max(quote => quote.EventNs);
        TimeSpan age = asOf - DateTimeOffset.FromUnixTimeMilliseconds(newestNs / 1_000_000L);
        return age < TimeSpan.FromMinutes(90)
            ? $"{age.TotalMinutes:N0} minutes old"
            : $"{age.TotalHours:N1} hours old";
    }

    /// <summary>
    /// Says whether the US options market is open, because outside regular hours every quote is stale
    /// by design and a report that omitted this would read as a fault.
    /// </summary>
    private static string SessionNote(DateTimeOffset asOf)
    {
        TimeSpan utc = asOf.UtcDateTime.TimeOfDay;
        bool weekday = asOf.UtcDateTime.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday;
        bool open = weekday && utc >= RegularHoursOpenUtc && utc < RegularHoursCloseUtc;
        return open
            ? "The US options market is open, so stale quotes here are a real problem."
            : "The US options market is closed (regular hours are 13:30-20:00 UTC), so stale quotes " +
              "are expected. Re-run during the session to test this properly.";
    }

    // Approximate: ignores US daylight-saving shifts and holidays, which is enough for a sentence whose
    // only job is to stop a closed market reading as a broken feed.
    private static readonly TimeSpan RegularHoursOpenUtc = new(13, 30, 0);
    private static readonly TimeSpan RegularHoursCloseUtc = new(20, 0, 0);

    private static OptionPreflightStep DescribeDiscovery(
        OptionContractQuery discovered, IReadOnlyList<AlpacaOptionContract> sample)
    {
        string excluded = discovered.Excluded.Count == 0
            ? "none excluded"
            : $"{discovered.Excluded.Count} excluded ({string.Join("; ", discovered.Excluded
                .Take(3).Select(item => $"{item.Symbol}: {item.Reason}"))})";

        return new OptionPreflightStep(
            "contract discovery",
            sample.Count > 0 ? OptionPreflightOutcome.Passed : OptionPreflightOutcome.Failed,
            $"{discovered.Contracts.Count} published, {excluded}, {sample.Count} sampled");
    }

    private static async Task<OptionPreflightStep> RunStepAsync(
        string name, Func<Task<(bool Passed, string Detail)>> run)
    {
        try
        {
            (bool passed, string detail) = await run();
            return new OptionPreflightStep(
                name, passed ? OptionPreflightOutcome.Passed : OptionPreflightOutcome.Failed, detail);
        }
        catch (Exception exception) when (IsVenueFailure(exception))
        {
            return new OptionPreflightStep(name, OptionPreflightOutcome.Failed, Describe(exception));
        }
    }

    private static OptionPreflightStep Skipped(string name) =>
        new(name, OptionPreflightOutcome.Skipped, "no tradable contract was discovered to price");

    /// <summary>
    /// A failure the venue or the transport produced. Deliberately does not catch everything: an
    /// argument error is this system misusing its own client and should surface as a crash, not as a
    /// tidy line in a report that reads like the venue's fault.
    /// </summary>
    private static bool IsVenueFailure(Exception exception) =>
        exception is HttpRequestException or InvalidDataException or TaskCanceledException or IOException;

    private static string Describe(Exception exception) => exception.Message;
}
