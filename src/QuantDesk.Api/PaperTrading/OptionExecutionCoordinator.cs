using QuantDesk.Domain.Capabilities;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Strategies;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Execution;
using QuantDesk.Domain.Options;
using QuantDesk.Runtime.Options;
using QuantDesk.Runtime.Persistence;

namespace QuantDesk.Api.PaperTrading;

/// <summary>What happened when a directional view was taken all the way to a broker submission.</summary>
public sealed record OptionExecutionOutcome(
    bool Submitted,
    string Reason,
    string? ExecutionId,
    string? EntryClientOrderId,
    decimal DefinedMaximumLoss,
    decimal NetDebitPerSpread,
    MultiLegExecutionState? State)
{
    public static OptionExecutionOutcome Rejected(string reason) =>
        new(false, reason, null, null, 0m, 0m, null);
}

/// <summary>
/// Carries a directional view from an option chain all the way into the durable multi-leg
/// execution lifecycle.
///
/// This was the last missing link in the options path. The compiler could produce a risk-defined
/// spread and the lifecycle could execute one durably, but nothing joined them, so an options
/// order could never be submitted no matter what research said.
///
/// The coordinator owns exactly that join and no policy of its own. It refuses before the broker
/// is contacted when the account does not permit the asset class, when no admissible spread
/// exists, or when the reservation cannot be persisted — reservation is committed to durable
/// storage before any POST, so an interrupted submission is recoverable by deterministic client
/// order ID rather than lost.
/// </summary>
public sealed class OptionExecutionCoordinator(
    OptionVerticalOpportunityService opportunities,
    MultiLegExecutionLifecycle lifecycle,
    ILogger<OptionExecutionCoordinator> logger)
{
    /// <summary>
    /// Buffer added to the compiled net debit when pricing the entry limit. The compiler already
    /// prices conservatively (pays the offer, receives the bid); this absorbs the quote moving
    /// between compilation and arrival without ever exceeding the risk budget, because the
    /// defined maximum loss is recomputed from the limit actually submitted.
    /// </summary>
    private const decimal EntryLimitBufferFraction = 0.02m;

    /// <summary>
    /// The exit is posted as a limit at a fraction of the debit, so a managed close still bounds
    /// its own fill. The lifecycle owns when it fires; this only prices it.
    /// </summary>
    private const decimal ExitLimitFraction = 0.50m;

    /// <param name="underlying">
    /// The equity whose directional view is being expressed. The option chain is discovered at
    /// runtime, so the caller configures a stable underlying rather than an OCC symbol that
    /// expires and could never be held in static configuration.
    /// </param>
    public async Task<OptionExecutionOutcome> ExecuteAsync(
        string underlying,
        AccountCapabilities capabilities,
        string executionId,
        decimal underlyingPrice,
        double expectedReturnBps,
        decimal riskBudget,
        PositionManagementPlan managementPlan,
        DateTimeOffset asOf,
        int minimumDaysToExpiry,
        int maximumDaysToExpiry,
        decimal strikeBandFraction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentException.ThrowIfNullOrWhiteSpace(underlying);
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);

        // A defined-risk vertical is a spread, which Alpaca gates at options level 2 or above.
        if (!capabilities.PaperEnvironment || !capabilities.OptionsTrading ||
            capabilities.OptionsTradingLevel < 2)
            return OptionExecutionOutcome.Rejected("AssetClassNotPermitted");

        OptionOpportunityOutcome opportunity = await opportunities.FindAsync(
            underlying, underlyingPrice, expectedReturnBps, candidateId: StableCandidateId(executionId),
            costBps: ExecutionCostProfile.UsEquityOption.HurdleBps(0m), managementPlan, asOf,
            minimumDaysToExpiry, maximumDaysToExpiry, strikeBandFraction, cancellationToken);

        if (!opportunity.Admitted || opportunity.Compilation?.Candidate is not { } candidate)
            return OptionExecutionOutcome.Rejected(opportunity.Reason);
        if (opportunity.SymbolsBySlot is not { } symbolsBySlot)
            return OptionExecutionOutcome.Rejected("MissingContractSymbolMapping");

        if (!TryBuildLegs(candidate, symbolsBySlot, out MultiLegExecutionLeg[]? legs) || legs is null)
            return OptionExecutionOutcome.Rejected("UnresolvedLegSymbol");

        decimal entryLimit = decimal.Round(
            candidate.NetLimitPrice * (1m + EntryLimitBufferFraction), 2, MidpointRounding.ToPositiveInfinity);
        decimal exitLimit = decimal.Round(
            candidate.NetLimitPrice * ExitLimitFraction, 2, MidpointRounding.ToZero);
        if (entryLimit <= 0 || exitLimit <= 0)
            return OptionExecutionOutcome.Rejected("UnpriceableSpread");

        // The debit actually payable is the submitted limit, not the compiled mid. Re-check it
        // against the budget so the buffer above can never widen the worst case past the cap.
        decimal worstCaseLoss = entryLimit * candidate.Legs[0].Ratio * OptionMultiplier;
        if (worstCaseLoss > riskBudget)
            return OptionExecutionOutcome.Rejected("EntryLimitExceedsRiskBudget");

        // A vertical pays OCC and regulatory fees per contract per side, which do not shrink with
        // order size. Below a certain debit those fixed cents exceed the edge the spread can win,
        // making the trade a loss whichever way the underlying moves. Refuse rather than pay the
        // broker to take a position with no upside left.
        if (!ExecutionCostProfile.UsEquityOption.IsEconomicallyViable(
                worstCaseLoss, (decimal)Math.Abs(expectedReturnBps), spreadBps: 0m, out string viability))
        {
            logger.LogInformation(
                "Option opportunity refused as uneconomic at {Notional}: {Reason}. Minimum viable is {Minimum}.",
                worstCaseLoss, viability,
                ExecutionCostProfile.UsEquityOption.MinimumViableNotionalUsd(
                    (decimal)Math.Abs(expectedReturnBps)));
            return OptionExecutionOutcome.Rejected(viability);
        }

        if (!lifecycle.TryReserve(
                executionId, candidate.StrategyId, quantity: 1, entryLimit, exitLimit,
                worstCaseLoss, managementPlan.MaximumHoldingPeriod, legs))
            return OptionExecutionOutcome.Rejected("ReservationRejected");

        logger.LogInformation(
            "Reserved multi-leg execution {ExecutionId} for {Strategy} with defined maximum loss {Loss}.",
            executionId, candidate.StrategyId, worstCaseLoss);

        MultiLegExecutionRecord record = await lifecycle.AdvanceAsync(executionId, cancellationToken);
        return new OptionExecutionOutcome(
            Submitted: record.State is not MultiLegExecutionState.EntryReserved,
            Reason: record.FailureReason ?? record.State.ToString(),
            ExecutionId: executionId,
            EntryClientOrderId: record.EntryCommand.ClientOrderId,
            DefinedMaximumLoss: worstCaseLoss,
            NetDebitPerSpread: entryLimit,
            State: record.State);
    }

    /// <summary>Standard US equity option contract size; the compiler rejects anything else.</summary>
    private const int OptionMultiplier = 100;

    /// <summary>
    /// Maps compiled legs from instrument slots to OCC symbols and to the open-side intents the
    /// broker requires. A leg whose slot cannot be resolved fails the whole spread rather than
    /// being dropped, because a partial spread is not risk-defined.
    /// </summary>
    private static bool TryBuildLegs(
        MultiLegOptionCandidate candidate,
        IReadOnlyDictionary<int, string> symbolsBySlot,
        out MultiLegExecutionLeg[]? legs)
    {
        legs = null;
        var built = new MultiLegExecutionLeg[candidate.Legs.Count];
        for (int index = 0; index < candidate.Legs.Count; index++)
        {
            OptionLegCandidate leg = candidate.Legs[index];
            if (!symbolsBySlot.TryGetValue(leg.ContractSlot, out string? symbol) ||
                string.IsNullOrWhiteSpace(symbol))
                return false;
            built[index] = new MultiLegExecutionLeg(
                symbol,
                leg.Ratio,
                leg.Side,
                leg.Side == OrderSide.Buy ? PositionIntent.BuyToOpen : PositionIntent.SellToOpen);
        }

        legs = built;
        return true;
    }

    /// <summary>
    /// Derives a stable candidate identity from the execution identity, so re-running the same
    /// opportunity produces the same candidate id rather than a fresh one.
    /// </summary>
    private static long StableCandidateId(string executionId)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(executionId));
        return Math.Abs(BitConverter.ToInt64(hash, 0) % long.MaxValue);
    }
}
