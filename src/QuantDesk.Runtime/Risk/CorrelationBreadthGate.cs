namespace QuantDesk.Runtime.Risk;

/// <summary>What the projected book would actually be exposed to, and whether that is allowed.</summary>
/// <param name="Allowed">False when the new position would push correlated exposure past the limit.</param>
/// <param name="Reason">Operator-readable cause, or null when allowed.</param>
/// <param name="NominalExposure">The sum of position sizes, which is what a position count implies.</param>
/// <param name="CorrelatedExposure">The same book measured through its correlation matrix.</param>
/// <param name="EffectiveBets">How many independent positions the book is really carrying.</param>
public readonly record struct CorrelationBreadthDecision(
    bool Allowed,
    string? Reason,
    decimal NominalExposure,
    decimal CorrelatedExposure,
    double EffectiveBets);

/// <summary>
/// Refuses a position that adds exposure without adding a bet.
///
/// The hole this fills
/// -------------------
/// The risk envelope capped notional, stress loss, daily and campaign loss, position count and
/// dollar delta. Nothing capped correlation, so the governor sized every position as though it were
/// independent of the others. On 2026-09-02 the lane held seven crypto symbols whose mean pairwise
/// correlation measured 0.709 -- about 1.33 independent bets carried as if they were seven. The
/// open-risk limit was satisfied at every moment and the account was concentrated the whole time.
/// Diversification was the assumption; correlation was the fact.
///
/// What is measured
/// ----------------
/// Correlated exposure is the portfolio standard deviation with equal per-position volatility:
///
///     sqrt( sum_i sum_j rho_ij * w_i * w_j )
///
/// For n equally sized positions at average correlation r that is w * sqrt(n + n(n-1)r). Seven
/// 200-dollar positions at 0.709 come to 1,213 dollars of correlated exposure where seven
/// independent ones would be 529. Effective bets is the same quantity read the other way round,
/// n / (1 + (n-1)r), which is the 1.33 above.
///
/// Correlations are used as measured, negatives included, because a position that genuinely moves
/// against the book does add breadth and clamping that away would refuse the most useful trade
/// available. Insufficient history is not treated as zero correlation -- assuming independence is
/// exactly the error this exists to prevent -- so a pair with too little overlapping history is
/// charged the conservative bound of 1.0 instead.
/// </summary>
public static class CorrelationBreadthGate
{
    /// <summary>Overlapping observations a pair needs before its correlation is believed.</summary>
    public const int MinimumOverlappingReturns = 30;

    /// <summary>
    /// The correlation assumed for a pair with too little history.
    ///
    /// One, not zero. A pair that cannot be measured might be independent or might be the same bet
    /// twice, and only one of those two mistakes costs money.
    /// </summary>
    public const double UnmeasurableCorrelation = 1.0;

    /// <summary>
    /// Whether adding <paramref name="candidateSymbol"/> keeps correlated exposure within the limit.
    /// </summary>
    /// <param name="candidateSymbol">The instrument being considered.</param>
    /// <param name="heldSymbols">Instruments the lane already holds.</param>
    /// <param name="closesBySymbol">Recent closing prices per symbol, oldest first.</param>
    /// <param name="positionNotional">The size of each position, assumed equal across the book.</param>
    /// <param name="maximumCorrelatedExposure">The cap, in the same currency as the notional.</param>
    public static CorrelationBreadthDecision Evaluate(
        string candidateSymbol,
        IReadOnlyList<string> heldSymbols,
        IReadOnlyDictionary<string, IReadOnlyList<decimal>> closesBySymbol,
        decimal positionNotional,
        decimal maximumCorrelatedExposure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateSymbol);
        ArgumentNullException.ThrowIfNull(heldSymbols);
        ArgumentNullException.ThrowIfNull(closesBySymbol);

        // A book of one is one bet whatever its correlation to nothing.
        List<string> book = [candidateSymbol, .. heldSymbols.Where(item =>
            !string.Equals(item, candidateSymbol, StringComparison.OrdinalIgnoreCase))];

        int n = book.Count;
        decimal nominal = positionNotional * n;
        if (n <= 1 || positionNotional <= 0m || maximumCorrelatedExposure <= 0m)
            return new CorrelationBreadthDecision(true, null, nominal, positionNotional, 1d);

        Dictionary<string, double[]> returns = new(StringComparer.OrdinalIgnoreCase);
        foreach (string symbol in book)
        {
            returns[symbol] = closesBySymbol.TryGetValue(symbol, out IReadOnlyList<decimal>? closes)
                ? LogReturns(closes)
                : [];
        }

        double sum = 0d;
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                sum += i == j ? 1d : Correlation(returns[book[i]], returns[book[j]]);
            }
        }

        // The matrix is a correlation matrix, so the sum cannot be negative for a real book; guard
        // anyway rather than take the root of a negative produced by an estimation artefact.
        double correlatedUnits = Math.Sqrt(Math.Max(sum, 1d));
        decimal correlated = positionNotional * (decimal)correlatedUnits;
        double effectiveBets = n * n / Math.Max(sum, 1d);

        return correlated <= maximumCorrelatedExposure
            ? new CorrelationBreadthDecision(true, null, nominal, correlated, effectiveBets)
            : new CorrelationBreadthDecision(
                false,
                $"CorrelatedExposureLimit:{correlated:0.00}>{maximumCorrelatedExposure:0.00} "
                    + $"({n} positions, {effectiveBets:0.00} effective bets)",
                nominal,
                correlated,
                effectiveBets);
    }

    /// <summary>Log returns, which are additive across time and symmetric in direction.</summary>
    private static double[] LogReturns(IReadOnlyList<decimal> closes)
    {
        if (closes.Count < 2) return [];

        List<double> returns = new(closes.Count - 1);
        for (int i = 1; i < closes.Count; i++)
        {
            double previous = (double)closes[i - 1];
            double current = (double)closes[i];
            if (previous <= 0d || current <= 0d) continue;
            returns.Add(Math.Log(current / previous));
        }

        return [.. returns];
    }

    /// <summary>
    /// Pearson correlation over the most recent overlapping window, or the conservative bound.
    ///
    /// The two series are aligned from their ends rather than their starts, because they are time
    /// series of different lengths ending at the same moment: aligning from the start would compare
    /// last week's returns on one symbol against yesterday's on the other and report a correlation
    /// that describes nothing.
    /// </summary>
    private static double Correlation(double[] left, double[] right)
    {
        int overlap = Math.Min(left.Length, right.Length);
        if (overlap < MinimumOverlappingReturns) return UnmeasurableCorrelation;

        ReadOnlySpan<double> a = left.AsSpan(left.Length - overlap);
        ReadOnlySpan<double> b = right.AsSpan(right.Length - overlap);

        double meanA = 0d, meanB = 0d;
        for (int i = 0; i < overlap; i++) { meanA += a[i]; meanB += b[i]; }
        meanA /= overlap;
        meanB /= overlap;

        double covariance = 0d, varianceA = 0d, varianceB = 0d;
        for (int i = 0; i < overlap; i++)
        {
            double da = a[i] - meanA;
            double db = b[i] - meanB;
            covariance += da * db;
            varianceA += da * da;
            varianceB += db * db;
        }

        // A flat series has no variance and therefore no measurable relationship. Charged the
        // conservative bound for the same reason as too little history.
        if (varianceA <= 0d || varianceB <= 0d) return UnmeasurableCorrelation;

        double correlation = covariance / Math.Sqrt(varianceA * varianceB);
        return double.IsFinite(correlation) ? Math.Clamp(correlation, -1d, 1d) : UnmeasurableCorrelation;
    }
}
