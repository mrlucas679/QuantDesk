namespace QuantDesk.Runtime.Costs;

/// <summary>Alpaca Tier 1 spot-crypto fee schedule used when account volume is unavailable.</summary>
public sealed record CryptoFeeSchedule(
    int Tier,
    decimal MakerBps,
    decimal TakerBps,
    string Broker,
    string Source,
    DateTimeOffset RetrievedAt)
{
    public static CryptoFeeSchedule AlpacaTier1(DateTimeOffset retrievedAt) => new(
        1, 15m, 25m, "Alpaca",
        "https://docs.alpaca.markets/us/docs/crypto-fees", retrievedAt);
}
