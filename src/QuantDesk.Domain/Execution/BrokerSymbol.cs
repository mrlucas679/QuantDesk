namespace QuantDesk.Domain.Execution;

/// <summary>
/// Compares an instrument symbol as this system writes it against the venue's own spelling.
///
/// Alpaca returns spot crypto positions without the separator -- "UNIUSD" where the system says
/// "UNI/USD" -- so an ordinal comparison never matches and every crypto position looks like it does
/// not exist. That failure is silent and dangerous in both directions: a lookup that should find a
/// position concludes there is nothing there, and a reconciliation that should notice unattributed
/// exposure sees none.
///
/// This lives in the domain because two assemblies need the same answer and were each deciding for
/// themselves. It had existed as an internal helper in the API layer, which is why the execution
/// lifecycle -- in a different assembly -- ended up with a plain string comparison instead, and
/// with it a live position it believed was already gone.
/// </summary>
public static class BrokerSymbol
{
    public static bool Matches(string? left, string? right) => string.Equals(
        Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string? value) =>
        (value ?? string.Empty).Replace("/", string.Empty, StringComparison.Ordinal).Trim();
}
