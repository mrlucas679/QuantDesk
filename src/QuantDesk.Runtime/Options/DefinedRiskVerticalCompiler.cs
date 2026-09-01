using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Options;
using QuantDesk.Domain.Runtime;
using QuantDesk.Domain.Strategies;
using QuantDesk.Domain.Trading;

namespace QuantDesk.Runtime.Options;

/// <summary>Why a directional view could not be expressed as a defined-risk vertical.</summary>
public enum VerticalRejection
{
    None,
    NoDirectionalConviction,
    NoExpiryInWindow,
    NoStrikePair,
    QuoteUnhealthy,
    SpreadTooWide,
    NetDebitNotPositive,
    DebitExceedsRiskBudget,
    DebitExceedsSpreadWidth,
    RewardToRiskTooLow,
    ExpectedValueBelowCosts
}

/// <summary>The compiled spread, or the reason no admissible spread exists.</summary>
public sealed record VerticalCompilation(
    MultiLegOptionCandidate? Candidate,
    VerticalRejection Rejection,
    Usd DefinedMaximumLoss,
    Usd MaximumProfit,
    decimal NetDebitPerSpread,
    decimal Breakeven)
{
    public bool Admitted => Candidate is not null && Rejection == VerticalRejection.None;

    /// <summary>
    /// Expected payoff at the forecast price, less the debit and the round-trip execution charge.
    ///
    /// Already computed to decide admission; kept so that admissible spreads can be ranked against
    /// each other instead of the first one found being taken.
    /// </summary>
    public decimal NetExpectedValue { get; init; }

    /// <summary>
    /// The wider of the two legs' quoted spreads, as a fraction of mid. Reported because it is the
    /// dominant execution cost on a real chain: SPY near-the-money spreads measured 0.61% at the
    /// tightest against a 3.93% median on 2026-09-01, and a vertical crosses both legs both ways.
    /// </summary>
    public double WidestLegRelativeSpread { get; init; }
}

/// <summary>
/// Turns a directional view on an underlying into a two-leg debit vertical whose worst case is
/// known and capped before the order is sent.
///
/// Why a debit vertical rather than a single long option or anything short-premium: the maximum
/// loss of a debit spread is exactly the net premium paid. It cannot gap through a stop, it cannot
/// be assigned into an unbounded liability, and it requires no margin beyond the debit. The
/// compiler refuses to emit a candidate whose debit exceeds the caller's risk budget, so the most
/// the application can lose on one options opportunity is a number chosen in advance.
///
/// Every rejection is typed. A caller that receives <see cref="VerticalRejection.SpreadTooWide"/>
/// learns something different from one that receives
/// <see cref="VerticalRejection.RewardToRiskTooLow"/>, and neither is reported as a generic
/// failure.
/// </summary>
public sealed class DefinedRiskVerticalCompiler(
    Usd riskBudgetPerSpread,
    double maximumRelativeSpread,
    double minimumRewardToRisk,
    int minimumDaysToExpiry,
    int maximumDaysToExpiry)
{
    /// <summary>
    /// Compiles the cheapest admissible vertical expressing <paramref name="expectedReturnBps"/>.
    /// </summary>
    /// <param name="candidateId">Deterministic identity carried onto the candidate.</param>
    /// <param name="underlyingPrice">Last trade or mid price of the underlying.</param>
    /// <param name="expectedReturnBps">Signed directional forecast; sign selects calls or puts.</param>
    /// <param name="contracts">Chain slice already restricted to one underlying.</param>
    /// <param name="quotes">Live quote per contract slot.</param>
    /// <param name="asOf">Session date used for expiry filtering.</param>
    /// <param name="costBps">Round-trip venue cost charged against the expected payoff.</param>
    /// <param name="managementPlan">Exit ownership attached to the emitted candidate.</param>
    public VerticalCompilation Compile(
        long candidateId,
        decimal underlyingPrice,
        double expectedReturnBps,
        IReadOnlyList<OptionContractDefinition> contracts,
        IReadOnlyDictionary<int, OptionQuoteSnapshot> quotes,
        DateOnly asOf,
        decimal costBps,
        PositionManagementPlan managementPlan)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        ArgumentNullException.ThrowIfNull(quotes);
        ArgumentNullException.ThrowIfNull(managementPlan);
        if (underlyingPrice <= 0 || !double.IsFinite(expectedReturnBps) || expectedReturnBps == 0)
            return Rejected(VerticalRejection.NoDirectionalConviction);

        bool bullish = expectedReturnBps > 0;
        OptionRight right = bullish ? OptionRight.Call : OptionRight.Put;
        OptionContractDefinition[] eligible = contracts
            .Where(contract => contract.Right == right && IsWithinExpiryWindow(contract.Expiration, asOf))
            .OrderBy(contract => contract.Expiration)
            .ThenBy(contract => contract.Strike)
            .ToArray();
        if (eligible.Length < 2) return Rejected(VerticalRejection.NoExpiryInWindow);

        decimal targetPrice = underlyingPrice * (1m + (decimal)expectedReturnBps / 10_000m);
        VerticalCompilation best = Rejected(VerticalRejection.NoStrikePair);

        foreach (IGrouping<DateOnly, OptionContractDefinition> expiry in
                 eligible.GroupBy(contract => contract.Expiration))
        {
            OptionContractDefinition[] strikes = [.. expiry];
            for (int lower = 0; lower < strikes.Length - 1; lower++)
            {
                for (int upper = lower + 1; upper < strikes.Length; upper++)
                {
                    VerticalCompilation attempt = TryPair(
                        candidateId, strikes[lower], strikes[upper], bullish, targetPrice,
                        quotes, costBps, managementPlan);
                    best = Prefer(best, attempt);
                }
            }
        }

        return best;
    }

    private VerticalCompilation TryPair(
        long candidateId,
        OptionContractDefinition lower,
        OptionContractDefinition upper,
        bool bullish,
        decimal targetPrice,
        IReadOnlyDictionary<int, OptionQuoteSnapshot> quotes,
        decimal costBps,
        PositionManagementPlan managementPlan)
    {
        // A bull call buys the lower strike and sells the higher one; a bear put buys the higher
        // strike and sells the lower. Either way the long leg is the more expensive one, which is
        // what makes the position a debit and therefore risk-defined.
        OptionContractDefinition longLeg = bullish ? lower : upper;
        OptionContractDefinition shortLeg = bullish ? upper : lower;

        if (!quotes.TryGetValue(longLeg.Id.Value, out OptionQuoteSnapshot longQuote) ||
            !quotes.TryGetValue(shortLeg.Id.Value, out OptionQuoteSnapshot shortQuote) ||
            !IsHealthy(longQuote) || !IsHealthy(shortQuote))
            return Rejected(VerticalRejection.QuoteUnhealthy);
        if (longQuote.RelativeSpread > maximumRelativeSpread ||
            shortQuote.RelativeSpread > maximumRelativeSpread)
            return Rejected(VerticalRejection.SpreadTooWide);

        // Pay the offer on the leg bought and receive the bid on the leg sold. Assuming mid fills
        // would understate the debit, and the debit is the maximum loss.
        decimal longPremium = (decimal)longQuote.Ask;
        decimal shortPremium = (decimal)shortQuote.Bid;
        decimal netDebitPerShare = longPremium - shortPremium;
        if (netDebitPerShare <= 0) return Rejected(VerticalRejection.NetDebitNotPositive);

        int multiplier = longLeg.Multiplier;
        (Usd maxLoss, Usd maxProfit, decimal breakeven) = bullish
            ? DefinedRiskPayoff.BullCallDebitSpread(
                lower.Strike, upper.Strike, longPremium, shortPremium, multiplier)
            : DefinedRiskPayoff.BearPutDebitSpread(
                lower.Strike, upper.Strike, longPremium, shortPremium, multiplier);

        if (maxProfit.Value <= 0) return Rejected(VerticalRejection.DebitExceedsSpreadWidth);
        if (maxLoss.Value > riskBudgetPerSpread.Value)
            return Rejected(VerticalRejection.DebitExceedsRiskBudget, maxLoss, maxProfit, netDebitPerShare, breakeven);
        if ((double)(maxProfit.Value / maxLoss.Value) < minimumRewardToRisk)
            return Rejected(VerticalRejection.RewardToRiskTooLow, maxLoss, maxProfit, netDebitPerShare, breakeven);

        // Value the spread at the forecast price, then charge the round trip against the debit.
        decimal intrinsicAtTarget = bullish
            ? Math.Clamp(targetPrice - lower.Strike, 0m, upper.Strike - lower.Strike)
            : Math.Clamp(upper.Strike - targetPrice, 0m, upper.Strike - lower.Strike);
        decimal expectedPayoff = intrinsicAtTarget * multiplier;
        decimal costCharge = maxLoss.Value * costBps / 10_000m;
        decimal netExpectedValue = expectedPayoff - maxLoss.Value - costCharge;
        if (netExpectedValue <= 0)
            return Rejected(VerticalRejection.ExpectedValueBelowCosts, maxLoss, maxProfit, netDebitPerShare, breakeven);

        var candidate = new MultiLegOptionCandidate(
            candidateId,
            bullish ? "spy-bull-call-vertical-v1" : "spy-bear-put-vertical-v1",
            [
                new OptionLegCandidate(longLeg.Id.Value, OrderSide.Buy, PositionIntent.Open, 1),
                new OptionLegCandidate(shortLeg.Id.Value, OrderSide.Sell, PositionIntent.Open, 1)
            ],
            decimal.Round(netDebitPerShare, 2, MidpointRounding.ToPositiveInfinity),
            maxLoss,
            managementPlan with { MaximumAdverseLoss = maxLoss });

        return new VerticalCompilation(
            candidate, VerticalRejection.None, maxLoss, maxProfit, netDebitPerShare, breakeven)
        {
            NetExpectedValue = netExpectedValue,
            WidestLegRelativeSpread = Math.Max(longQuote.RelativeSpread, shortQuote.RelativeSpread)
        };
    }

    private bool IsWithinExpiryWindow(DateOnly expiration, DateOnly asOf)
    {
        int days = expiration.DayNumber - asOf.DayNumber;
        return days >= minimumDaysToExpiry && days <= maximumDaysToExpiry;
    }

    private static bool IsHealthy(OptionQuoteSnapshot quote) =>
        quote.Quality == DataQuality.Healthy &&
        double.IsFinite(quote.Bid) && double.IsFinite(quote.Ask) &&
        quote.Bid >= 0 && quote.Ask > 0 && quote.Bid <= quote.Ask;

    /// <summary>Keeps the most informative rejection so the caller learns the real obstacle.</summary>
    /// <summary>
    /// Ranks one compilation against another.
    ///
    /// Between two admissible spreads, the one with more expected value after costs wins. The search
    /// used to return the *first* admissible pair in ascending strike order, which is an arbitrary
    /// choice dressed as a decision: on a measured SPY chain the tightest near-the-money spread was
    /// 0.61% against a 3.93% median, and a vertical crosses both legs in both directions — so taking
    /// the first admissible pair rather than the best one can pay several times the necessary
    /// execution cost while reporting nothing unusual.
    ///
    /// Between two refusals the later-stage rejection wins, because a spread refused for
    /// <see cref="VerticalRejection.RewardToRiskTooLow"/> got further than one refused for
    /// <see cref="VerticalRejection.QuoteUnhealthy"/> and says more about why nothing qualified.
    /// </summary>
    private static VerticalCompilation Prefer(VerticalCompilation current, VerticalCompilation attempt)
    {
        if (attempt.Admitted && current.Admitted)
            return attempt.NetExpectedValue > current.NetExpectedValue ? attempt : current;
        if (attempt.Admitted) return attempt;
        if (current.Admitted) return current;
        return attempt.Rejection > current.Rejection ? attempt : current;
    }

    private static VerticalCompilation Rejected(
        VerticalRejection rejection,
        Usd? maxLoss = null,
        Usd? maxProfit = null,
        decimal netDebit = 0m,
        decimal breakeven = 0m) =>
        new(null, rejection, maxLoss ?? Usd.Zero, maxProfit ?? Usd.Zero, netDebit, breakeven);
}
