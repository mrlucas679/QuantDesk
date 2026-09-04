using QuantDesk.Domain.Contracts;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Strategies;
using QuantDesk.Runtime.State;

namespace QuantDesk.Runtime.Costs;

/// <summary>Whether a cost figure rests on measurement or only on a model.</summary>
public enum CostBasis
{
    /// <summary>No measured dataset covered this order size. The number is an assumption.</summary>
    Modelled,

    /// <summary>Measured round trips covered this size, and the model did not understate them.</summary>
    MeasuredAndModelAgrees,

    /// <summary>Measured round trips cost more than the model predicted, so measurement governs.</summary>
    MeasuredExceedsModel
}

/// <param name="Estimate">The cost to charge.</param>
/// <param name="Basis">Where that number came from, so a caller can refuse to act on an assumption.</param>
public readonly record struct PricedCost(CostEstimate Estimate, CostBasis Basis)
{
    /// <summary>True when real round trips back this figure at this order size.</summary>
    public bool IsMeasured => Basis is not CostBasis.Modelled;
}

/// <summary>
/// Supplies the current realised-cost dataset.
///
/// A source rather than a snapshot, because the dataset is only ever as good as the round trips
/// behind it and a value captured at startup would keep answering after new evidence arrived. The
/// implementation derives it on read for the same reason.
/// </summary>
public interface IRealisedCostSource
{
    /// <summary>The dataset as it stands now, or null when too little has been measured.</summary>
    RealisedCostContract? Current();

    /// <summary>
    /// How many completed round trips could testify, and why the rest could not.
    ///
    /// A null dataset says nothing about whether the system has traded and cannot measure, or has
    /// not traded at all. Those call for opposite responses, and telling them apart used to require
    /// reading the durable store by hand.
    /// </summary>
    RealisedCostCoverage Coverage() => default;
}

/// <summary>
/// Charges the greater of the modelled cost and what trading this size actually cost.
///
/// Why a floor rather than a replacement
/// -------------------------------------
/// The modelled components are not guesses in equal measure. The spread term is read from the live
/// quote at decision time and is the best available estimate of a cost that varies minute to minute;
/// the measured dataset is an average over trips taken under conditions that no longer hold. Taking
/// the maximum keeps whichever is currently more pessimistic, so a widening spread still raises the
/// charge, and a fee the model has never heard of still cannot be traded through.
///
/// The failure this prevents is specific and was live in this system. The C# scenarios charged
/// Alpaca's published 50 bps schedule rate. The account lost 68 bps per round trip, because the
/// venue also levies a USD cash charge that appears in neither the fill price nor the filled
/// quantity. Every candidate whose expected edge sat between those two numbers looked profitable
/// and was not.
/// </summary>
public sealed class MeasuredCostFloor(ICostModel modelled, IRealisedCostSource measured) : ICostModel
{
    public CostEstimate Estimate(in TradeCandidate candidate, in InstrumentSnapshot market) =>
        Price(candidate, market).Estimate;

    /// <summary>The cost, together with whether measurement or assumption produced it.</summary>
    public PricedCost Price(in TradeCandidate candidate, in InstrumentSnapshot market)
    {
        CostEstimate modelledEstimate = modelled.Estimate(candidate, market);
        decimal notional = candidate.Exposure.Notional.Value;

        if (measured.Current()?.UpperConfidenceCostBpsFor(notional) is not { } measuredBps || notional <= 0m)
            return new(modelledEstimate, CostBasis.Modelled);

        Usd measuredTotal = new(notional * measuredBps / 10_000m);
        if (measuredTotal <= modelledEstimate.Total)
            return new(modelledEstimate, CostBasis.MeasuredAndModelAgrees);

        return new(
            modelledEstimate with { MeasuredExcess = measuredTotal - modelledEstimate.Total },
            CostBasis.MeasuredExceedsModel);
    }
}
