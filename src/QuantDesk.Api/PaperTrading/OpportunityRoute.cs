using QuantDesk.Domain.Capabilities;
using QuantDesk.Domain.Options;
using QuantDesk.Domain.Trading;

namespace QuantDesk.Api.PaperTrading;

/// <summary>The asset classes this application is allowed to route an opportunity to.</summary>
/// <summary>
/// How an admitted opportunity is priced into an order.
///
/// The autonomous lane previously hardcoded <see cref="ExecutionOrderType.Market"/>. That is the
/// most expensive and least safe choice available: on spot crypto it guarantees the 25 bps taker
/// rate on both legs, and on any book it accepts whatever price the venue returns, with no cap on
/// how far through the quote the fill lands. A marketable limit crosses the spread far enough to
/// fill promptly while still refusing a price worse than the caller sanctioned, which bounds the
/// loss a thin or fast-moving book can inflict.
/// </summary>
/// <param name="OrderType">Order type submitted to the broker.</param>
/// <param name="TimeInForce">Time in force submitted to the broker.</param>
/// <param name="MarketableOffsetBps">
/// How far beyond the touch a limit is priced. Zero for a true market order. Large enough to fill
/// against normal quote movement, small enough to bound an adverse fill.
/// </param>
public sealed record OrderExecutionPolicy(
    ExecutionOrderType OrderType,
    ExecutionTimeInForce TimeInForce,
    decimal MarketableOffsetBps)
{
    /// <summary>Crosses the spread by a bounded amount and refuses anything worse.</summary>
    public static readonly OrderExecutionPolicy MarketableLimit =
        new(ExecutionOrderType.Limit, ExecutionTimeInForce.Ioc, 10m);

    /// <summary>Unbounded price acceptance. Retained only for lanes that explicitly opt in.</summary>
    public static readonly OrderExecutionPolicy UnboundedMarket =
        new(ExecutionOrderType.Market, ExecutionTimeInForce.Ioc, 0m);

    /// <summary>Returns the limit price for a buy, or null when the policy is a market order.</summary>
    public decimal? BuyLimitPrice(decimal ask) =>
        OrderType == ExecutionOrderType.Market || ask <= 0
            ? null
            : decimal.Round(ask * (1m + MarketableOffsetBps / 10_000m), 8, MidpointRounding.ToPositiveInfinity);

    /// <summary>Returns the limit price for a sell, or null when the policy is a market order.</summary>
    public decimal? SellLimitPrice(decimal bid) =>
        OrderType == ExecutionOrderType.Market || bid <= 0
            ? null
            : decimal.Round(bid * (1m - MarketableOffsetBps / 10_000m), 8, MidpointRounding.ToNegativeInfinity);
}

/// <summary>
/// Everything the runtime needs to admit and price one opportunity in one asset class: the venue
/// cost that decides admissibility, the order policy that bounds the fill, and the account
/// permission the class requires.
///
/// Before this existed the autonomous lane hardcoded all three to spot crypto in four separate
/// places, so adding an asset class meant editing the decision pipeline rather than adding a
/// route. Every unsupported symbol now fails closed at one place instead of silently taking the
/// crypto path.
/// </summary>
public sealed record OpportunityRoute(
    string Symbol,
    TradedAssetClass AssetClass,
    ExecutionCostProfile Costs,
    OrderExecutionPolicy OrderPolicy)
{
    /// <summary>True only when the live account actually permits trading this asset class.</summary>
    public bool IsPermittedBy(AccountCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!capabilities.PaperEnvironment) return false;
        return AssetClass switch
        {
            TradedAssetClass.SpotCrypto => capabilities.CryptoTrading,
            TradedAssetClass.UsEquity => capabilities.EquityTrading,
            // A defined-risk vertical is a spread, which Alpaca gates at options level 2 or above.
            TradedAssetClass.UsEquityOption =>
                capabilities.OptionsTrading && capabilities.OptionsTradingLevel >= 2,
            _ => false
        };
    }
}

/// <summary>
/// Classifies a symbol into exactly one supported route, or refuses it with a reason.
///
/// Classification is deliberately strict. An unrecognised symbol is never given a default route,
/// because a wrong route would apply the wrong cost model and the wrong permission check to real
/// money movement.
/// </summary>
public sealed class OpportunityRouter(ExecutionCostProfile? cryptoCosts = null)
{
    private readonly ExecutionCostProfile _cryptoCosts = cryptoCosts ?? ExecutionCostProfile.SpotCryptoTaker;

    public bool TryRoute(string? symbol, out OpportunityRoute? route, out string reason)
    {
        route = null;
        if (string.IsNullOrWhiteSpace(symbol))
        {
            reason = "SymbolMissing";
            return false;
        }

        string normalized = symbol.Trim().ToUpperInvariant();
        if (OccOptionSymbol.TryParse(normalized, out OccOptionSymbol? occ) && occ is not null)
        {
            route = new OpportunityRoute(
                occ.BrokerSymbol, TradedAssetClass.UsEquityOption,
                ExecutionCostProfile.UsEquityOption, OrderExecutionPolicy.MarketableLimit);
            reason = "Routed";
            return true;
        }

        if (normalized.Contains('/', StringComparison.Ordinal))
        {
            string[] parts = normalized.Split('/');
            if (parts.Length != 2 || parts.Any(part => part.Length is 0 or > 6) ||
                !parts.All(part => part.All(char.IsAsciiLetterOrDigit)))
            {
                reason = "UnsupportedCryptoPair";
                return false;
            }

            route = new OpportunityRoute(
                normalized, TradedAssetClass.SpotCrypto, _cryptoCosts,
                OrderExecutionPolicy.MarketableLimit);
            reason = "Routed";
            return true;
        }

        if (normalized.Length is >= 1 and <= 5 && normalized.All(char.IsAsciiLetterUpper))
        {
            route = new OpportunityRoute(
                normalized, TradedAssetClass.UsEquity,
                ExecutionCostProfile.UsEquity, OrderExecutionPolicy.MarketableLimit);
            reason = "Routed";
            return true;
        }

        reason = "UnsupportedSymbol";
        return false;
    }
}
