namespace QuantDesk.Domain.Instruments;

public enum AssetClass
{
    Equity = 1,
    Option = 2,
    CryptoSpot = 3
}

public enum InstrumentType
{
    Stock = 1,
    Etf = 2,
    Option = 3,
    CryptoPair = 4
}

public readonly record struct InstrumentId(int Value);

public readonly record struct RuntimeInstrument(
    int Slot,
    InstrumentId InstrumentId,
    AssetClass AssetClass,
    InstrumentType InstrumentType);

