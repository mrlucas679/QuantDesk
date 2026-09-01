using QuantDesk.Runtime.Persistence;

namespace QuantDesk.Api.PaperTrading;

/// <summary>
/// The arithmetic and state predicates behind the diagnostic lane's exposure, profit, and
/// lifecycle decisions.
///
/// Extracted from a 1,072-line service. These are pure functions over a record, and they decide
/// whether the lane believes it still holds something and whether a reconciliation failure gets an
/// honest reason. Both deserve to be readable without the persistence and broker code around them.
/// </summary>
public static class DiagnosticExecutionMath
{
    /// <summary>
    /// Quantity the application still believes it holds.
    ///
    /// The basis is the exit quantity once an exit has been sized, and the entry fill before that.
    /// Using the entry fill throughout would understate what remains after a partial exit, and a
    /// lane that understates its own exposure stops closing it. Clamped at zero so an over-fill
    /// reported by the venue cannot present as negative exposure.
    /// </summary>
    public static decimal InternalExposure(DiagnosticExecutionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        decimal flattenBasis = record.ExitQuantity > 0 ? record.ExitQuantity : record.EntryFilledQuantity;
        return Math.Max(0m, flattenBasis - record.ExitFilledQuantity - record.EmergencyFilledQuantity);
    }

    /// <summary>
    /// Realised paper profit, or null when the round trip is incomplete.
    ///
    /// Null rather than zero on purpose: an unfinished trade has no profit, and reporting zero
    /// would be indistinguishable from a genuine break-even.
    /// </summary>
    public static decimal? GrossPaperPnl(DiagnosticExecutionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.EntryAverageFillPrice is not decimal entryPrice ||
            record.ExitAverageFillPrice is not decimal exitPrice ||
            record.ExitFilledQuantity <= 0)
            return null;
        return (exitPrice - entryPrice) * record.ExitFilledQuantity;
    }

    /// <summary>
    /// Names the most specific reason a reconciliation failed, checked most-diagnostic first so
    /// the operator is told the actionable cause rather than a downstream symptom of it.
    /// </summary>
    public static string ReconciliationFailureReason(
        bool unresolvedOrders, decimal brokerQuantity, decimal internalQuantity)
    {
        if (unresolvedOrders) return "RECONCILIATION_UNRESOLVED_DIAGNOSTIC_ORDERS";
        if (brokerQuantity != internalQuantity) return "RECONCILIATION_BROKER_INTERNAL_MISMATCH";
        if (brokerQuantity != 0) return "RECONCILIATION_BROKER_EXPOSURE_REMAINS";
        return "RECONCILIATION_INTERNAL_EXPOSURE_REMAINS";
    }

    /// <summary>An entry that ended without leaving the lane anything further to do.</summary>
    public static bool IsTerminalEntryState(string state) =>
        state is "EntryCanceled" or "EntryRejected" or "EntryExpired" or
            "ReconciliationFailed" or "EmergencyFlattenFailed";

    /// <summary>Any state in which the exit, rather than the entry, is the active concern.</summary>
    public static bool IsExitLifecycleState(string state) => state is
        "ExitDue" or "ExitReserved" or "ExitSubmitted" or "ExitAccepted" or "ExitPartiallyFilled" or
        "ExitFilled" or "ExitSubmissionUnknown" or "ExitCanceled" or "ExitRejected" or "ExitExpired";

    /// <summary>
    /// An exit order that ended without filling. Terminal for the order, not for the lane: the
    /// position may still be open, so the caller must re-derive exposure rather than stop here.
    /// </summary>
    public static bool IsTerminalExitState(string state) =>
        state is "ExitCanceled" or "ExitRejected" or "ExitExpired";
}
