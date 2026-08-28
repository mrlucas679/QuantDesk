namespace QuantDesk.Domain.Capabilities;

public sealed record CapabilityReport(
    bool PaperEnvironment,
    bool EquityTrading,
    bool CryptoTrading,
    bool OptionsTrading,
    int? OptionsTradingLevel,
    bool TradeUpdateStream,
    bool NewsAvailable,
    string EquityFeed,
    string OptionFeed,
    int? EquitySubscriptionLimit,
    int? OptionSubscriptionLimit,
    IReadOnlyList<string> Problems,
    string? RequestId);
