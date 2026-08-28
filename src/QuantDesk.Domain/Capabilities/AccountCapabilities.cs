namespace QuantDesk.Domain.Capabilities;

public sealed record AccountCapabilities(
    bool PaperEnvironment,
    bool EquityTrading,
    bool CryptoTrading,
    bool OptionsTrading,
    int? OptionsTradingLevel);

