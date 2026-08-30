namespace QuantDesk.Runtime.Costs;

/// <summary>Provenance-backed US equity fee schedule used by offline research.</summary>
public sealed record EquityFeeSchedule(
    string Broker,
    string AssetClass,
    string ScheduleRevision,
    string Source,
    DateTimeOffset RetrievedAt,
    string DocumentTitle,
    string? DocumentSha256,
    decimal SecTransactionRate,
    decimal TafPerShare,
    decimal TafPerTradeCap,
    decimal CatPerShare,
    bool CommissionFree,
    string CommissionPolicy,
    string CommissionSource)
{
    /// <summary>Creates the published Alpaca Securities schedule for NMS equities.</summary>
    public static EquityFeeSchedule AlpacaUsNms(DateTimeOffset retrievedAt) => new(
        "Alpaca Securities LLC",
        "US NMS Equity",
        "2026-07-20",
        "https://alpaca.markets/disclosures",
        retrievedAt,
        "Alpaca Clearing Brokerage Fee Schedule",
        null,
        0.0000206m,
        0.000195m,
        9.79m,
        0.000003m,
        false,
        "ACCOUNT_SPECIFIC",
        "Account eligibility must be verified from the account agreement");

    /// <summary>Estimates sell-side regulatory fees for a filled equity order.</summary>
    public decimal RegulatoryFee(decimal notional, decimal shares, bool isSell)
    {
        if (notional < 0 || shares < 0) throw new ArgumentOutOfRangeException(nameof(shares));
        if (notional == 0 && shares == 0) return 0m;
        if (!isSell) return shares * CatPerShare;
        var sec = notional * SecTransactionRate;
        var taf = Math.Min(shares * TafPerShare, TafPerTradeCap);
        return sec + taf + shares * CatPerShare;
    }
}
