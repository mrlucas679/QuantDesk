using QuantDesk.Domain.Instruments;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Portfolio;
using QuantDesk.Domain.Trading;
using QuantDesk.Domain.Runtime;
using QuantDesk.Domain.Strategies;

namespace QuantDesk.Domain.Options;

public enum OptionRight { Call, Put }

public enum OptionExerciseStyle { American, European }

public sealed record OptionContractDefinition(
    InstrumentId Id,
    InstrumentId UnderlyingId,
    string BrokerSymbol,
    DateOnly Expiration,
    decimal Strike,
    OptionRight Right,
    int Multiplier);

public readonly record struct OptionQuoteSnapshot(
    int ContractSlot,
    double Bid,
    double Ask,
    double Mid,
    double RelativeSpread,
    long EventNs,
    DataQuality Quality);

/// <summary>Greeks returned by the authenticated option snapshot feed for risk admission.</summary>
public readonly record struct OptionRiskSnapshot(
    int ContractSlot,
    double ImpliedVolatility,
    double Delta,
    double Gamma,
    double Vega,
    double Theta,
    long EventNs,
    DataQuality Quality);

public readonly record struct OptionLegCandidate(
    int ContractSlot,
    OrderSide Side,
    PositionIntent Intent,
    int Ratio);

public sealed record MultiLegOptionCandidate(
    long CandidateId,
    string StrategyId,
    IReadOnlyList<OptionLegCandidate> Legs,
    decimal NetLimitPrice,
    Usd DefinedMaximumLoss,
    PositionManagementPlan ManagementPlan);
