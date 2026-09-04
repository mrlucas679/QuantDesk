using QuantDesk.Domain.Instruments;
using QuantDesk.Domain.Options;
using QuantDesk.Domain.Runtime;
using QuantDesk.Runtime.Options;

namespace QuantDesk.Runtime.Tests.Options;

public sealed class OptionChainValidatorTests
{
    [Fact]
    public void AcceptsHealthyNonCrossedChain()
    {
        var contract = new OptionContractDefinition(new InstrumentId(4), new InstrumentId(1), "AAPL2601C100", new DateOnly(2026, 9, 1), 100, OptionRight.Call, 100);
        var quote = new OptionQuoteSnapshot(4, 1.1, 1.2, 1.15, .09, 1, DataQuality.Healthy);
        Assert.True(OptionChainValidator.TryValidate([contract], new Dictionary<int, OptionQuoteSnapshot> { [4] = quote }, new DateOnly(2026, 8, 28), out _));
    }

    [Fact]
    public void RejectsCrossedOrStaleQuotes()
    {
        var contract = new OptionContractDefinition(new InstrumentId(4), new InstrumentId(1), "AAPL2601C100", new DateOnly(2026, 9, 1), 100, OptionRight.Call, 100);
        var quote = new OptionQuoteSnapshot(4, 1.3, 1.2, 1.25, .09, 1, DataQuality.Stale);
        Assert.False(OptionChainValidator.TryValidate([contract], new Dictionary<int, OptionQuoteSnapshot> { [4] = quote }, new DateOnly(2026, 8, 28), out string reason));
        Assert.Equal("invalid_quote", reason);
    }
}
