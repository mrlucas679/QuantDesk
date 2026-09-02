using QuantDesk.Domain.Trading;
using QuantDesk.Alpaca.MarketData;
using QuantDesk.Domain.Options;

namespace QuantDesk.Api.PaperTrading;

/// <summary>
/// Supplies directional market evidence for whichever asset class a route names.
///
/// The autonomous service previously called the crypto quote client directly, which is what made
/// the lane crypto-only in practice even after the gates were generalised: there was no way for an
/// equity symbol to obtain evidence. Selection now follows the route, and an asset class with no
/// evidence source fails loudly rather than silently falling back to the crypto venue.
///
/// An option routes to its *underlying's* evidence. The directional view is formed on the
/// underlying, and the spread is only the instrument used to express it — pricing the option chain
/// is a separate step owned by <see cref="OptionVerticalOpportunityService"/>.
/// </summary>
/// <summary>
/// Supplies directional evidence for a route. An interface because the autonomous evaluation cycle
/// depends on it, and the money path must be substitutable to be testable at all.
/// </summary>
public interface IMarketEvidenceProvider
{
    Task<DirectionalMarketEvidence> GetEvidenceAsync(
        OpportunityRoute route, CancellationToken cancellationToken);
}

public sealed class MarketEvidenceProvider(
    AlpacaLatestCryptoQuoteClient cryptoQuotes,
    AlpacaLatestEquityQuoteClient equityQuotes) : IMarketEvidenceProvider
{
    public Task<DirectionalMarketEvidence> GetEvidenceAsync(
        OpportunityRoute route, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(route);
        return route.AssetClass switch
        {
            TradedAssetClass.SpotCrypto =>
                cryptoQuotes.GetEvidenceAsync(route.Symbol, cancellationToken),
            TradedAssetClass.UsEquity =>
                equityQuotes.GetEvidenceAsync(route.Symbol, cancellationToken),
            TradedAssetClass.UsEquityOption =>
                equityQuotes.GetEvidenceAsync(UnderlyingOf(route.Symbol), cancellationToken),
            _ => throw new NotSupportedException(
                $"No market-evidence source is configured for {route.AssetClass}.")
        };
    }

    /// <summary>Extracts the underlying ticker an option's directional view is formed on.</summary>
    public static string UnderlyingOf(string optionSymbol) =>
        OccOptionSymbol.TryParse(optionSymbol, out OccOptionSymbol? parsed) && parsed is not null
            ? parsed.Underlying
            : throw new ArgumentException(
                $"'{optionSymbol}' is not a valid OCC option symbol.", nameof(optionSymbol));
}
