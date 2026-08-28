namespace QuantDesk.Domain.Instruments;

public sealed record InstrumentMetadata(
    InstrumentId Id,
    string Symbol,
    AssetClass AssetClass,
    InstrumentType InstrumentType,
    bool Tradable,
    bool Shortable,
    bool HasOptions,
    bool OvernightTradable);

public sealed record SubscriptionBudget(
    int EquitySymbols,
    int OptionQuotes,
    int CryptoSymbols);

public sealed class SubscriptionPlan
{
    public required IReadOnlyList<InstrumentId> Equity { get; init; }

    public required IReadOnlyList<InstrumentId> Crypto { get; init; }

    public required IReadOnlyList<InstrumentId> Options { get; init; }
}

public interface IAssetDiscoveryGateway
{
    Task<IReadOnlyList<InstrumentMetadata>> GetAssetsAsync(CancellationToken cancellationToken);
}

