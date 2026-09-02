using System.Globalization;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Risk;

namespace QuantDesk.Api.PaperTrading;

/// <summary>
/// Builds the runtime risk envelope from configuration, scaled to the account's order notional.
///
/// These limits were previously ten unnamed literals inline in the composition root, with no
/// configuration, no override, and no provenance. Two of the resulting values were actively
/// misleading:
///
/// * The three greeks caps — dollar delta, dollar gamma per 1%, and dollar vega per vol point —
///   were all set to 100,000 against a $20 order envelope. They could never bind, so the system
///   presented greeks risk limits it did not actually enforce. That mattered little while the only
///   lane was spot crypto, which has no gamma or vega at all; it matters now that a defined-risk
///   options lane exists, because greeks are precisely how option risk is measured.
/// * The loss caps were absolute dollars unrelated to the configured notional, so changing the
///   order size silently changed how many losing trades the envelope tolerated.
///
/// Limits are now derived from the order notional by default, so the envelope scales with the
/// position size instead of drifting away from it, and every multiplier is overridable.
/// </summary>
public static class RiskLimitOptions
{
    /// <summary>One trade may risk its full notional in the worst modelled case, and no more.</summary>
    private const decimal DefaultStressLossPerTradeMultiple = 1.0m;

    /// <summary>Total open risk across concurrent positions.</summary>
    private const decimal DefaultOpenRiskMultiple = 2.0m;

    /// <summary>A day's losses stop the lane at five times one order's notional.</summary>
    private const decimal DefaultDailyLossMultiple = 5.0m;

    /// <summary>Campaign-level stop.</summary>
    private const decimal DefaultCampaignLossMultiple = 10.0m;

    /// <summary>
    /// Directional exposure cap. A long spot position of one notional carries a dollar delta of
    /// about that notional, so a small multiple both permits the intended trade and bounds a
    /// mispriced one. This is the limit that was previously inert at 100,000.
    /// </summary>
    private const double DefaultDollarDeltaMultiple = 3.0;

    /// <summary>
    /// Convexity cap per 1% underlying move. A defined-risk vertical's gamma is a small fraction
    /// of its notional, so this binds well before an unintended naked position could pass.
    /// </summary>
    private const double DefaultDollarGammaMultiple = 1.0;

    /// <summary>Volatility exposure cap per vol point, sized the same way as gamma.</summary>
    private const double DefaultDollarVegaMultiple = 1.0;

    /// <summary>
    /// Cap on the book's correlation-adjusted exposure.
    ///
    /// Three times one order's notional permits about nine genuinely independent positions, or
    /// about three that move as one. The distinction is the entire point: a position count treats
    /// those two books as identical, and on 2026-09-02 the account carried seven crypto positions
    /// worth 1,213 dollars of correlated exposure while every configured limit read as satisfied.
    ///
    /// Kept here rather than in RiskLimits because it is evaluated at the lane's entry gate, where
    /// the candidate's return history is available. Folding it into the governor's marginal-risk
    /// evaluation is the better home and the next step.
    /// </summary>
    private const decimal DefaultCorrelatedExposureMultiple = 3.0m;

    /// <inheritdoc cref="DefaultCorrelatedExposureMultiple"/>
    public static decimal MaximumCorrelatedExposure(decimal orderNotional) =>
        Positive(
            "QUANTDESK_RISK_MAX_CORRELATED_EXPOSURE",
            orderNotional * DefaultCorrelatedExposureMultiple);

    public static RiskLimits FromEnvironment(decimal orderNotional)
    {
        if (orderNotional <= 0)
            throw new ArgumentOutOfRangeException(nameof(orderNotional), "Order notional must be positive.");

        var limits = new RiskLimits(
            MaximumStressLossPerTrade: Money("QUANTDESK_RISK_STRESS_LOSS_PER_TRADE",
                orderNotional * DefaultStressLossPerTradeMultiple),
            MaximumOpenRisk: Money("QUANTDESK_RISK_MAX_OPEN_RISK",
                orderNotional * DefaultOpenRiskMultiple),
            MaximumDailyLoss: Money("QUANTDESK_RISK_MAX_DAILY_LOSS",
                orderNotional * DefaultDailyLossMultiple),
            MaximumCampaignLoss: Money("QUANTDESK_RISK_MAX_CAMPAIGN_LOSS",
                orderNotional * DefaultCampaignLossMultiple),
            MaximumOpenPositions: Count("QUANTDESK_RISK_MAX_OPEN_POSITIONS", 1),
            MaximumAbsDollarDelta: Number("QUANTDESK_RISK_MAX_DOLLAR_DELTA",
                (double)orderNotional * DefaultDollarDeltaMultiple),
            MaximumAbsDollarGamma1Pct: Number("QUANTDESK_RISK_MAX_DOLLAR_GAMMA",
                (double)orderNotional * DefaultDollarGammaMultiple),
            MaximumAbsDollarVega1Vol: Number("QUANTDESK_RISK_MAX_DOLLAR_VEGA",
                (double)orderNotional * DefaultDollarVegaMultiple),
            MaximumRelativeSpread: Number("QUANTDESK_RISK_MAX_RELATIVE_SPREAD", 0.01),
            MaximumShortConvexityScore: Number("QUANTDESK_RISK_MAX_SHORT_CONVEXITY", 1));

        limits.Validate();
        return limits;
    }

    private static Usd Money(string variable, decimal fallback) => new(Positive(variable, fallback));

    private static decimal Positive(string variable, decimal fallback) =>
        decimal.TryParse(
            Environment.GetEnvironmentVariable(variable),
            NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed) && parsed > 0
            ? parsed
            : fallback;

    private static int Count(string variable, int fallback) =>
        int.TryParse(
            Environment.GetEnvironmentVariable(variable),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
            ? parsed
            : fallback;

    private static double Number(string variable, double fallback) =>
        double.TryParse(
            Environment.GetEnvironmentVariable(variable),
            NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
        double.IsFinite(parsed) && parsed > 0
            ? parsed
            : fallback;
}
