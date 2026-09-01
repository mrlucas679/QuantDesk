using QuantDesk.Runtime.Persistence;

namespace QuantDesk.Api.PaperTrading;

/// <summary>
/// Claims from the durable stores of each execution lane.
///
/// Each adapter reads only nonterminal records, so exposure stops being claimed the moment its record
/// completes. A store that cannot be read claims nothing, which fails closed: unreadable state means
/// exposure is reported as unattributed, and unattributed exposure halts entry.
/// </summary>
public sealed class DiagnosticExposureClaimSource(DiagnosticExecutionStore store) : IExposureClaimSource
{
    public string LaneName => "diagnostic";

    public IReadOnlyList<ExposureClaim> ListClaims()
    {
        try
        {
            return [.. store.ListNonterminal().Select(record => new ExposureClaim(
                record.Symbol,
                [.. new[] { record.EntryClientOrderId, record.ExitClientOrderId, record.EmergencyClientOrderId }
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Select(id => id!)]))];
        }
        catch (Exception exception) when (DiagnosticFailureClassification.IsPersistenceFailure(exception))
        {
            return [];
        }
    }
}

/// <summary>Claims from the spot lane's durable store.</summary>
public sealed class SpotExposureClaimSource(SpotExecutionStore store) : IExposureClaimSource
{
    public string LaneName => "spot";

    public IReadOnlyList<ExposureClaim> ListClaims()
    {
        try
        {
            return [.. store.ListNonterminal().Select(record => new ExposureClaim(
                record.Symbol,
                [record.EntryClientOrderId, record.ExitClientOrderId]))];
        }
        catch (Exception exception) when (DiagnosticFailureClassification.IsPersistenceFailure(exception))
        {
            return [];
        }
    }
}

/// <summary>
/// Claims from the multi-leg lane's durable store.
///
/// A spread claims each of its option legs by OCC symbol, because that is what the venue reports as a
/// position — a multi-leg order fills into per-leg positions, not into one position for the spread.
/// </summary>
public sealed class MultiLegExposureClaimSource(MultiLegExecutionStore store) : IExposureClaimSource
{
    public string LaneName => "multi-leg";

    public IReadOnlyList<ExposureClaim> ListClaims()
    {
        try
        {
            return
            [
                .. store.ListNonterminal().SelectMany(record =>
                {
                    string[] clientOrderIds =
                    [
                        record.EntryCommand.ClientOrderId,
                        record.ExitCommand.ClientOrderId
                    ];
                    return record.EntryCommand.Legs
                        .Select(leg => leg.Symbol)
                        .Distinct(StringComparer.Ordinal)
                        .Select(symbol => new ExposureClaim(symbol, clientOrderIds));
                })
            ];
        }
        catch (Exception exception) when (DiagnosticFailureClassification.IsPersistenceFailure(exception))
        {
            return [];
        }
    }
}
