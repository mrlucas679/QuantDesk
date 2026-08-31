using QuantDesk.Alpaca.Mapping;
using QuantDesk.Alpaca.MarketData;
using QuantDesk.Domain.Instruments;
using QuantDesk.Domain.Options;
using QuantDesk.Domain.Strategies;
using QuantDesk.Runtime.Options;

namespace QuantDesk.Api.PaperTrading;

/// <summary>Why a directional view on an underlying produced no tradable spread.</summary>
public sealed record OptionOpportunityOutcome(
    VerticalCompilation? Compilation,
    string Reason,
    int ContractsConsidered,
    int ContractsPriced,
    // The compiled candidate identifies its legs by instrument slot. Execution needs OCC symbols,
    // and only this service knows the mapping, so it travels with the outcome rather than being
    // reconstructed downstream where a mismatch would go unnoticed.
    IReadOnlyDictionary<int, string>? SymbolsBySlot = null)
{
    public bool Admitted => Compilation is { Admitted: true };
}

/// <summary>
/// Turns a directional view on an underlying into a priced, risk-defined vertical spread.
///
/// This is the join that was missing between the options pieces the repository already had.
/// Contracts could be discovered and a spread could be compiled, but nothing connected discovery
/// to live pricing to compilation, so no options candidate could ever exist. The service does
/// exactly that and nothing else: it selects a bounded strike band around spot, prices those
/// contracts, and hands them to the compiler.
///
/// It deliberately narrows the universe before pricing. Alpaca accepts at most 100 symbols per
/// quote request, and quoting an entire chain to pick two strikes would be both slow and wasteful,
/// so only strikes within a band around the underlying are considered.
/// </summary>
public sealed class OptionVerticalOpportunityService(
    AlpacaOptionContractClient contracts,
    AlpacaLatestOptionQuoteClient quotes,
    IInstrumentSymbolResolver symbols,
    DefinedRiskVerticalCompiler compiler,
    ILogger<OptionVerticalOpportunityService> logger)
{
    /// <summary>Alpaca prices at most 100 contracts per request.</summary>
    private const int MaximumPricedContracts = 100;
    private static readonly TimeSpan MaximumQuoteAge = TimeSpan.FromMinutes(2);

    public async Task<OptionOpportunityOutcome> FindAsync(
        string underlying,
        decimal underlyingPrice,
        double expectedReturnBps,
        long candidateId,
        decimal costBps,
        PositionManagementPlan managementPlan,
        DateTimeOffset asOf,
        int minimumDaysToExpiry,
        int maximumDaysToExpiry,
        decimal strikeBandFraction,
        CancellationToken cancellationToken)
    {
        if (underlyingPrice <= 0 || !double.IsFinite(expectedReturnBps) || expectedReturnBps == 0)
            return new OptionOpportunityOutcome(null, "NoDirectionalConviction", 0, 0);

        DateOnly today = DateOnly.FromDateTime(asOf.UtcDateTime);
        OptionContractQuery discovered = await contracts.ListAsync(
            underlying,
            today.AddDays(minimumDaysToExpiry),
            today.AddDays(maximumDaysToExpiry),
            "active",
            cancellationToken);

        OptionRight right = expectedReturnBps > 0 ? OptionRight.Call : OptionRight.Put;
        decimal band = underlyingPrice * strikeBandFraction;
        AlpacaOptionContract[] selected = discovered.Contracts
            .Where(contract => contract.Right == right && contract.Tradable &&
                               Math.Abs(contract.Strike - underlyingPrice) <= band)
            .OrderBy(contract => contract.Expiration)
            .ThenBy(contract => Math.Abs(contract.Strike - underlyingPrice))
            .Take(MaximumPricedContracts)
            .ToArray();
        if (selected.Length < 2)
            return new OptionOpportunityOutcome(null, "NoStrikeBandCoverage", discovered.Contracts.Count, 0);

        var slotsBySymbol = new Dictionary<string, int>(StringComparer.Ordinal);
        var definitions = new List<OptionContractDefinition>(selected.Length);
        foreach (AlpacaOptionContract contract in selected)
        {
            if (!symbols.TryRegisterOptionSymbol(contract.Symbol, out int slot))
            {
                logger.LogWarning(
                    "Skipping option contract that could not be assigned an instrument slot.");
                continue;
            }

            slotsBySymbol[contract.Symbol] = slot;
            definitions.Add(new OptionContractDefinition(
                new InstrumentId(slot),
                new InstrumentId(0),
                contract.Symbol,
                contract.Expiration,
                contract.Strike,
                contract.Right,
                contract.Multiplier));
        }

        if (slotsBySymbol.Count < 2)
            return new OptionOpportunityOutcome(null, "NoResolvableContracts", discovered.Contracts.Count, 0);

        IReadOnlyDictionary<int, OptionQuoteSnapshot> priced = await quotes.GetQuotesAsync(
            slotsBySymbol, asOf, MaximumQuoteAge, cancellationToken);

        VerticalCompilation compilation = compiler.Compile(
            candidateId, underlyingPrice, expectedReturnBps, definitions, priced, today,
            costBps, managementPlan);

        return new OptionOpportunityOutcome(
            compilation,
            compilation.Admitted ? "Admitted" : compilation.Rejection.ToString(),
            discovered.Contracts.Count,
            slotsBySymbol.Count,
            slotsBySymbol.ToDictionary(entry => entry.Value, entry => entry.Key));
    }
}
