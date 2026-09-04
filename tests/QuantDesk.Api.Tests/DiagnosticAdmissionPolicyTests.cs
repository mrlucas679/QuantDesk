using QuantDesk.Api.PaperTrading;
using QuantDesk.Domain.Execution;
using QuantDesk.Runtime.Persistence;

namespace QuantDesk.Api.Tests;

/// <summary>
/// These are the rules that stop an order. They lived inside a 1,072-line service and had no
/// direct coverage; extracting them made each refusal testable on its own.
/// </summary>
public sealed class DiagnosticAdmissionPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AHealthyAccountAndTradableAssetAdmitsEntry() =>
        Assert.Null(DiagnosticAdmissionPolicy.VerifyEntry(Context(), notional: 10m));

    [Theory]
    [InlineData("INACTIVE", "ACTIVE")]
    [InlineData("ACTIVE", "INACTIVE")]
    public void AnAccountNotFullyActiveIsRefused(string status, string cryptoStatus)
    {
        BrokerEntryContext context = Context(account: Account(status: status, cryptoStatus: cryptoStatus));

        Assert.Equal(
            DiagnosticAdmissionPolicy.AccountUnavailable,
            DiagnosticAdmissionPolicy.VerifyEntry(context, 10m)?.Reason);
    }

    [Fact]
    public void ABlockedAccountIsRefused()
    {
        Assert.NotNull(DiagnosticAdmissionPolicy.VerifyEntry(
            Context(account: Account(tradingBlocked: true)), 10m));
        Assert.NotNull(DiagnosticAdmissionPolicy.VerifyEntry(
            Context(account: Account(accountBlocked: true)), 10m));
    }

    [Fact]
    public void EntryRequiresBuyingPowerToCoverTheNotional()
    {
        BrokerEntryContext context = Context(account: Account(buyingPower: 5m));

        Assert.Equal(
            DiagnosticAdmissionPolicy.AccountUnavailable,
            DiagnosticAdmissionPolicy.VerifyEntry(context, notional: 10m)?.Reason);
    }

    [Fact]
    public void ExitDoesNotRequireBuyingPowerBecauseRefusingToCloseWouldStrandExposure()
    {
        // Entry is refused for want of buying power; the same account must still be allowed to
        // close, or a funding shortfall would trap a live position.
        BrokerEntryContext context = Context(account: Account(buyingPower: 0m, equity: 0m));

        Assert.NotNull(DiagnosticAdmissionPolicy.VerifyEntry(context, 10m));
        Assert.Null(DiagnosticAdmissionPolicy.VerifyExit(context));
    }

    [Fact]
    public void AMissingAccountOrAssetIsTreatedAsARefusalNotAnUnknown()
    {
        // The lane cannot distinguish "the venue said no" from "the venue did not answer", so
        // both must fail closed.
        // Built directly rather than through the defaulting helper, so "absent" really is absent.
        var noAccount = new BrokerEntryContext(null, Asset(), null, [], []);
        var noAsset = new BrokerEntryContext(Account(), null, null, [], []);

        Assert.Equal(
            DiagnosticAdmissionPolicy.AccountUnavailable,
            DiagnosticAdmissionPolicy.VerifyEntry(noAccount, 10m)?.Reason);
        Assert.Equal(
            DiagnosticAdmissionPolicy.SymbolNotTradable,
            DiagnosticAdmissionPolicy.VerifyEntry(noAsset, 10m)?.Reason);
        Assert.Equal(
            DiagnosticAdmissionPolicy.AccountUnavailable,
            DiagnosticAdmissionPolicy.VerifyExit(noAccount)?.Reason);
        Assert.Equal(
            DiagnosticAdmissionPolicy.SymbolNotTradable,
            DiagnosticAdmissionPolicy.VerifyExit(noAsset)?.Reason);
    }

    [Theory]
    [InlineData(false, "active", "crypto")]
    [InlineData(true, "inactive", "crypto")]
    [InlineData(true, "active", "us_equity")]
    public void AnUntradableOrWrongClassAssetIsRefused(bool tradable, string status, string assetClass)
    {
        BrokerEntryContext context = Context(asset: Asset(tradable, status, assetClass));

        Assert.Equal(
            DiagnosticAdmissionPolicy.SymbolNotTradable,
            DiagnosticAdmissionPolicy.VerifyEntry(context, 10m)?.Reason);
    }

    [Fact]
    public void SymbolsMatchAcrossTheVenuesInconsistentSlashConvention()
    {
        // The trading API accepts BTC/USD while several position endpoints report BTCUSD.
        Assert.True(DiagnosticAdmissionPolicy.SymbolsMatch("BTC/USD", "BTCUSD"));
        Assert.True(DiagnosticAdmissionPolicy.SymbolsMatch("btcusd", "BTC/USD"));
        Assert.False(DiagnosticAdmissionPolicy.SymbolsMatch("ETH/USD", "BTC/USD"));
    }

    [Fact]
    public void AnOrderThisExperimentDidNotCreateBlocksReconciliation()
    {
        DiagnosticExecutionRecord record = Record();
        BrokerEntryContext context = Context(openOrders: [Order("someone-elses-order")]);

        Assert.True(DiagnosticAdmissionPolicy.HasUnknownOrder(record, context.OpenOrders));
        Assert.False(DiagnosticAdmissionPolicy.IsReconciled(record, context));
    }

    [Fact]
    public void ThisExperimentsOwnOrdersDoNotBlockReconciliation()
    {
        DiagnosticExecutionRecord record = Record();
        BrokerEntryContext context = Context(
            openOrders: [Order("entry-id"), Order("exit-id"), Order("emergency-id")]);

        Assert.False(DiagnosticAdmissionPolicy.HasUnknownOrder(record, context.OpenOrders));
    }

    [Fact]
    public void ReconciliationRequiresBrokerExposureToMatchWhatIsExplained()
    {
        DiagnosticExecutionRecord record = Record() with { EntryFilledQuantity = 0.5m };

        Assert.True(DiagnosticAdmissionPolicy.IsReconciled(
            record, Context(positions: [Position(0.5m)])));
        Assert.False(DiagnosticAdmissionPolicy.IsReconciled(
            record, Context(positions: [Position(0.9m)])));
    }

    [Fact]
    public void ZeroQuantityAndForeignSymbolPositionsAreIgnored()
    {
        IReadOnlyList<BrokerPositionSnapshot> relevant = DiagnosticAdmissionPolicy.RelevantPositions(
            [Position(0m), new BrokerPositionSnapshot("ETH/USD", 0, 5m, 100m), Position(0.25m)]);

        Assert.Single(relevant);
        Assert.Equal(0.25m, relevant[0].Quantity);
    }

    private static BrokerEntryContext Context(
        BrokerAccountSnapshot? account = null,
        BrokerAssetSnapshot? asset = null,
        IReadOnlyList<BrokerOrderSnapshot>? openOrders = null,
        IReadOnlyList<BrokerPositionSnapshot>? positions = null) =>
        new(account ?? Account(), asset ?? Asset(), null, openOrders ?? [], positions ?? []);

    private static BrokerAccountSnapshot Account(
        string status = "ACTIVE",
        string cryptoStatus = "ACTIVE",
        bool tradingBlocked = false,
        bool accountBlocked = false,
        decimal equity = 100_000m,
        decimal buyingPower = 100_000m) =>
        new("acct", status, equity, buyingPower, tradingBlocked, accountBlocked)
        {
            CryptoTradingStatus = cryptoStatus
        };

    private static BrokerAssetSnapshot Asset(
        bool tradable = true, string status = "active", string assetClass = "crypto") =>
        new("BTC/USD", status, assetClass, tradable);

    private static BrokerOrderSnapshot Order(string clientOrderId) =>
        new("broker-1", clientOrderId, "new", 0m, null);

    private static BrokerPositionSnapshot Position(decimal quantity) =>
        new("BTC/USD", 0, quantity, 100m);

    private static DiagnosticExecutionRecord Record() =>
        new("EXP-1", "diagnostic", "BTC/USD", "EntryReserved",
            RequestedNotional: 10m, HoldingDuration: TimeSpan.FromMinutes(2), CreatedAt: Now,
            EntryClientOrderId: "entry-id", ExitClientOrderId: "exit-id")
        {
            EmergencyClientOrderId = "emergency-id"
        };
}
