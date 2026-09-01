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
                slots, asOf, TimeSpan.FromMinutes(15), cancellationToken);
            int healthy = priced.Values.Count(quote => quote.Quality == DataQuality.Healthy);
            // A quote is what makes a defined-risk debit computable, so none usable is a failure even
            // though the venue answered successfully.
            return (healthy > 0,
                $"{healthy}/{priced.Count} usable" + (healthy > 0
                    ? $"; tightest relative spread {priced.Values
                        .Where(quote => quote.Quality == DataQuality.Healthy)
                        .Min(quote => quote.RelativeSpread):P2}"
                    : "; every quote was missing, crossed, one-sided or stale"));
        }));

        steps.Add(await RunStepAsync("greeks and implied volatility", async () =>
        {
            IReadOnlyDictionary<int, OptionRiskSnapshot> risk = await snapshots.GetSnapshotsAsync(
                slots, asOf, TimeSpan.FromMinutes(15), cancellationToken);
            int healthy = risk.Values.Count(item => item.Quality == DataQuality.Healthy);
            return (healthy > 0, $"{healthy}/{risk.Count} usable");
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
