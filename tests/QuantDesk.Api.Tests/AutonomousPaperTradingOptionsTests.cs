using QuantDesk.Api.PaperTrading;

namespace QuantDesk.Api.Tests;

public sealed class AutonomousPaperTradingOptionsTests : IDisposable
{
    private static readonly string[] Variables =
    [
        "QUANTDESK_AUTONOMOUS_ENABLED", "QUANTDESK_AUTONOMOUS_MODE",
        "QUANTDESK_AUTONOMOUS_SYMBOL"
    ];

    public AutonomousPaperTradingOptionsTests() => Clear();

    [Fact]
    public void EnabledAutonomyRequiresAnExplicitExecutionSymbol()
    {
        Environment.SetEnvironmentVariable("QUANTDESK_AUTONOMOUS_ENABLED", "true");
        Environment.SetEnvironmentVariable("QUANTDESK_AUTONOMOUS_MODE", "ValidatedPaper");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            AutonomousPaperTradingOptions.FromEnvironment(PaperOptions()));

        Assert.Contains("AUTONOMOUS_SYMBOL is required", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledResearchFallbackDoesNotEnableAutonomousExecution()
    {
        AutonomousPaperTradingOptions options = AutonomousPaperTradingOptions.FromEnvironment(PaperOptions());

        Assert.False(options.Enabled);
        Assert.Equal("BTC/USD", options.Symbol);
    }

    public void Dispose() => Clear();

    private static PaperTradingOptions PaperOptions() => new(1_000m,
        new Dictionary<int, string> { [0] = "BTC/USD", [1] = "SPY" });

    private static void Clear()
    {
        foreach (string variable in Variables) Environment.SetEnvironmentVariable(variable, null);
    }
}
