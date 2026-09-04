using QuantDesk.Domain.Execution;
using QuantDesk.Runtime.Persistence;

namespace QuantDesk.Api.PaperTrading;

/// <summary>Everything the broker reported when the diagnostic lane last looked.</summary>
public sealed record BrokerEntryContext(
    BrokerAccountSnapshot? Account,
    BrokerAssetSnapshot? Asset,
    BrokerOrderSnapshot? ExistingOrder,
    IReadOnlyList<BrokerOrderSnapshot> OpenOrders,
    IReadOnlyList<BrokerPositionSnapshot> Positions);

/// <summary>
/// Decides whether broker truth permits the diagnostic lane to enter, exit, or declare itself
/// reconciled.
///
/// Extracted from a 1,072-line service. Every decision here is a pure function of what the broker
/// reported, which is why it belongs outside a class that also owns persistence, submission, and
/// recovery: these are the rules that stop an order, and rules that stop orders should be readable
/// and testable on their own.
///
/// Each check fails closed. A missing account, a missing asset, or an unparseable status is
/// treated exactly like an explicit refusal, because the lane cannot distinguish "the venue said
/// no" from "the venue did not answer" safely enough to proceed on either.
/// </summary>
public static class DiagnosticAdmissionPolicy
{
    public const string AccountUnavailable = "PAPER_ACCOUNT_UNAVAILABLE";
    public const string SymbolNotTradable = "BTC_USD_NOT_TRADABLE";

    /// <summary>
    /// Entry admission. Stricter than exit: it additionally requires buying power to cover the
    /// notional and positive equity, because entry adds exposure while exit removes it.
    /// </summary>
    public static DiagnosticExecutionResult? VerifyEntry(BrokerEntryContext context, decimal notional)
    {
        ArgumentNullException.ThrowIfNull(context);

        BrokerAccountSnapshot? account = context.Account;
        if (account is null || account.TradingBlocked || account.AccountBlocked || account.Equity <= 0 ||
            account.BuyingPower < notional ||
            !IsActive(account.Status) || !IsActive(account.CryptoTradingStatus))
            return DiagnosticExecutionResult.Blocked(AccountUnavailable);

        return VerifyAsset(context.Asset);
    }

    /// <summary>
    /// Exit admission. Deliberately does not check buying power or equity: refusing to close a
    /// position because the account is short of buying power would strand live exposure, which is
    /// worse than the condition it would be guarding against.
    /// </summary>
    public static DiagnosticExecutionResult? VerifyExit(BrokerEntryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        BrokerAccountSnapshot? account = context.Account;
        if (account is null || account.TradingBlocked || account.AccountBlocked ||
            !IsActive(account.Status) || !IsActive(account.CryptoTradingStatus))
            return DiagnosticExecutionResult.Blocked(AccountUnavailable);

        return VerifyAsset(context.Asset);
    }

    /// <summary>
    /// The largest shortfall between a filled quantity and the resulting position that is still
    /// explained by fees rather than by exposure nobody owns.
    ///
    /// Alpaca charges crypto commission **in kind**: buy $10 of BTC and the position is a fraction of a
    /// percent smaller than the quantity the fill reported. Comparing the two exactly therefore fails on
    /// every successful crypto entry. Observed live at 0.25%; the bound is set at 0.5% so an ordinary
    /// fee never reads as unexplained exposure, while anything an order-sized error could produce still
    /// does.
    /// </summary>
    public const decimal MaximumInKindFeeShare = 0.005m;

    /// <summary>
    /// True only when broker exposure matches what this experiment can account for, and no order
    /// outside the experiment is open.
    ///
    /// "Matches" allows the position to fall short of the fill by up to <see cref="MaximumInKindFeeShare"/>,
    /// and not to exceed it at all. A position larger than what was filled is exposure this experiment
    /// did not create, which is the case this check exists to catch.
    /// </summary>
    public static bool IsReconciled(DiagnosticExecutionRecord record, BrokerEntryContext context)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(context);

        if (HasUnknownOrder(record, context.OpenOrders)) return false;

        decimal brokerQuantity = RelevantPositions(context.Positions).Sum(position => position.Quantity);
        decimal explainedQuantity = context.ExistingOrder?.FilledQuantity ?? record.EntryFilledQuantity;
        if (explainedQuantity <= 0) return brokerQuantity == explainedQuantity;

        decimal shortfall = explainedQuantity - brokerQuantity;
        return shortfall >= 0 && shortfall <= explainedQuantity * MaximumInKindFeeShare;
    }

    /// <summary>
    /// An open order this experiment did not create. Treated as a hard stop rather than ignored:
    /// an unattributed order may be about to change the exposure being reconciled.
    /// </summary>
    public static bool HasUnknownOrder(
        DiagnosticExecutionRecord record, IReadOnlyList<BrokerOrderSnapshot> openOrders)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(openOrders);
        return openOrders.Any(order => !IsDiagnosticOrder(record, order));
    }

    /// <summary>True when the order carries one of this experiment's own client order IDs.</summary>
    public static bool IsDiagnosticOrder(DiagnosticExecutionRecord record, BrokerOrderSnapshot order)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(order);
        return string.Equals(order.ClientOrderId, record.EntryClientOrderId, StringComparison.Ordinal) ||
               string.Equals(order.ClientOrderId, record.ExitClientOrderId, StringComparison.Ordinal) ||
               string.Equals(order.ClientOrderId, record.EmergencyClientOrderId, StringComparison.Ordinal);
    }

    /// <summary>Non-zero positions in the diagnostic symbol.</summary>
    public static IReadOnlyList<BrokerPositionSnapshot> RelevantPositions(
        IReadOnlyList<BrokerPositionSnapshot> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        return [.. positions
            .Where(position => SymbolsMatch(position.Symbol, DiagnosticExecutionOptions.RequiredSymbol))
            .Where(position => position.Quantity != 0)];
    }

    /// <summary>
    /// Compares symbols across the venue's inconsistent slash convention: the trading API accepts
    /// <c>BTC/USD</c> while several data and position endpoints report <c>BTCUSD</c>.
    /// </summary>
    public static bool SymbolsMatch(string left, string right) => string.Equals(
        (left ?? string.Empty).Replace("/", string.Empty, StringComparison.Ordinal),
        (right ?? string.Empty).Replace("/", string.Empty, StringComparison.Ordinal),
        StringComparison.OrdinalIgnoreCase);

    private static DiagnosticExecutionResult? VerifyAsset(BrokerAssetSnapshot? asset) =>
        asset is not null && asset.Tradable &&
        string.Equals(asset.Status, "active", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(asset.AssetClass, "crypto", StringComparison.OrdinalIgnoreCase) &&
        SymbolsMatch(asset.Symbol, DiagnosticExecutionOptions.RequiredSymbol)
            ? null
            : DiagnosticExecutionResult.Blocked(SymbolNotTradable);

    private static bool IsActive(string? status) =>
        string.Equals(status, "ACTIVE", StringComparison.OrdinalIgnoreCase);
}
