using QuantDesk.Alpaca.Mapping;
using QuantDesk.Domain.Execution;
using QuantDesk.Runtime.Persistence;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.PaperTrading;

/// <summary>What an emergency flatten attempt established about the position.</summary>
public enum DiagnosticEmergencyFlattenOutcome
{
    /// <summary>Could not act. The record is unchanged and the same attempt can be made again.</summary>
    Refused,

    /// <summary>The flatten failed, and the record now says so. This is terminal.</summary>
    Failed,

    /// <summary>A flatten order is live at the venue. Nothing further to do until it resolves.</summary>
    Working,

    /// <summary>Exposure is gone. The caller owes a final reconciliation.</summary>
    Flat
}

/// <summary>
/// The result of one attempt, carrying the reason whenever the attempt did not leave the lane flat.
/// </summary>
public sealed record DiagnosticEmergencyFlattenResult(
    DiagnosticEmergencyFlattenOutcome Outcome,
    string? ReasonCode)
{
    public static DiagnosticEmergencyFlattenResult Refused(string reason) =>
        new(DiagnosticEmergencyFlattenOutcome.Refused, reason);

    public static DiagnosticEmergencyFlattenResult Failed(string reason) =>
        new(DiagnosticEmergencyFlattenOutcome.Failed, reason);

    public static DiagnosticEmergencyFlattenResult Working { get; } =
        new(DiagnosticEmergencyFlattenOutcome.Working, null);

    public static DiagnosticEmergencyFlattenResult Flat { get; } =
        new(DiagnosticEmergencyFlattenOutcome.Flat, null);
}

/// <summary>
/// Closes a diagnostic position that must not stay open, and nothing else.
///
/// This is the last-resort path: it runs when the managed exit did not work, or when a restart found a
/// half-finished flatten. It was the final piece still living inside the diagnostic service, where the
/// code that gives up on a position sat beside the code that opens one. Separating them matters because
/// the two answer to opposite instincts — entry must fail closed and refuse when anything is uncertain,
/// while a flatten must keep going, since refusing to act is what leaves exposure open.
///
/// The order of work is what makes it safe to run repeatedly:
///
/// 1. If a flatten order already exists under this experiment's client order ID, track that one. This is
///    checked first so a restart mid-submission resumes rather than submits again.
/// 2. Cancel this experiment's other orders, then re-read them. A working entry or exit could otherwise
///    fill while the flatten is being sized, and the flatten would close the wrong quantity.
/// 3. Read broker positions. If there is nothing there, the position is already gone and the answer is
///    <see cref="DiagnosticEmergencyFlattenOutcome.Flat"/> without sending anything.
/// 4. Only then claim the submission and send, sizing to broker truth rather than to what the record
///    believed it held.
///
/// The claim in step 4 is durable and single-shot: a record that has already attempted a submission is
/// refused rather than retried, because a duplicate flatten would open a short position in the act of
/// trying to close a long one.
///
/// It never reconciles. Reporting <see cref="DiagnosticEmergencyFlattenOutcome.Flat"/> and letting the
/// caller reconcile keeps this class free of any dependency on the service that owns it.
/// </summary>
public sealed class DiagnosticEmergencyFlatten(
    DiagnosticExecutionStore store,
    IBrokerExecutionGateway broker,
    IInstrumentSymbolResolver symbols,
    IRuntimeClock clock)
{
    /// <summary>
    /// Advances the flatten by one step. Safe to call repeatedly: every step is derived from broker truth
    /// and the durable record, so a caller that calls once per recovery cycle drives it to completion.
    /// </summary>
    public async Task<DiagnosticEmergencyFlattenResult> FlattenAsync(
        string experimentId,
        CancellationToken cancellationToken)
    {
        DiagnosticExecutionRecord? record = store.Find(experimentId);
        if (record is null) return DiagnosticEmergencyFlattenResult.Refused("DIAGNOSTIC_NOT_FOUND");
        if (!symbols.TryResolveBySymbol(DiagnosticExecutionOptions.RequiredSymbol, out int instrumentSlot))
            return DiagnosticEmergencyFlattenResult.Refused("BTC_USD_INSTRUMENT_UNAVAILABLE");
        if (!broker.IsPaperEnvironment)
            return DiagnosticEmergencyFlattenResult.Refused("ALPACA_PAPER_REQUIRED");
        if (string.IsNullOrWhiteSpace(record.EmergencyClientOrderId))
            return DiagnosticEmergencyFlattenResult.Refused("EMERGENCY_CLIENT_ID_UNAVAILABLE");

        try
        {
            BrokerOrderSnapshot? existing = await broker.FindByClientOrderIdAsync(
                record.EmergencyClientOrderId,
                cancellationToken);
            if (existing is not null) return Track(record, existing);

            DiagnosticEmergencyFlattenResult? cancelFailure = await CancelOwnOrdersAsync(record, cancellationToken);
            if (cancelFailure is not null) return cancelFailure;

            IReadOnlyList<BrokerPositionSnapshot> positions = await broker.ListPositionsAsync(cancellationToken);
            decimal brokerQuantity = DiagnosticAdmissionPolicy.RelevantPositions(positions)
                .Sum(position => position.Quantity);
            if (brokerQuantity == 0)
            {
                store.Update(experimentId, current => current with { State = "Reconciling" });
                return DiagnosticEmergencyFlattenResult.Flat;
            }

            // A short position cannot be closed by the sell this lane knows how to send, and guessing at
            // a buy would be a new position rather than a flatten.
            if (brokerQuantity < 0) return Fail(record, "EMERGENCY_SHORT_EXPOSURE_UNSUPPORTED");
            if (record.EmergencySubmissionAttemptedAt is not null)
                return Fail(record, "EMERGENCY_SUBMISSION_UNKNOWN");

            if (!store.TryClaimEmergencySubmission(
                    experimentId,
                    brokerQuantity,
                    clock.UtcNow,
                    out DiagnosticExecutionRecord? claimed))
                return DiagnosticEmergencyFlattenResult.Refused("EMERGENCY_SUBMISSION_UNKNOWN");

            return await SubmitAsync(claimed!, instrumentSlot, cancellationToken);
        }
        catch (Exception exception)
            when (DiagnosticFailureClassification.IsInfrastructureFailure(exception, cancellationToken))
        {
            return DiagnosticEmergencyFlattenResult.Refused("RECONCILIATION_UNAVAILABLE");
        }
    }

    /// <summary>
    /// Cancels this experiment's own working orders and confirms they are gone.
    ///
    /// Re-reading rather than trusting the cancel is the point: an order that is still working could fill
    /// between here and the flatten, and the flatten would then be sized against a position that changed
    /// underneath it. An unconfirmed cancel is a failure, not something to proceed past.
    /// </summary>
    private async Task<DiagnosticEmergencyFlattenResult?> CancelOwnOrdersAsync(
        DiagnosticExecutionRecord record,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<BrokerOrderSnapshot> openOrders = await broker.ListOpenOrdersForSymbolAsync(
            DiagnosticExecutionOptions.RequiredSymbol,
            cancellationToken);
        foreach (BrokerOrderSnapshot order in
            openOrders.Where(order => DiagnosticAdmissionPolicy.IsDiagnosticOrder(record, order)))
            await broker.CancelAsync(order.BrokerOrderId, cancellationToken);

        IReadOnlyList<BrokerOrderSnapshot> remaining = await broker.ListOpenOrdersForSymbolAsync(
            DiagnosticExecutionOptions.RequiredSymbol,
            cancellationToken);
        return remaining.Any(order => DiagnosticAdmissionPolicy.IsDiagnosticOrder(record, order))
            ? Fail(record, "EMERGENCY_CANCEL_UNCONFIRMED")
            : null;
    }

    private async Task<DiagnosticEmergencyFlattenResult> SubmitAsync(
        DiagnosticExecutionRecord record,
        int instrumentSlot,
        CancellationToken cancellationToken)
    {
        try
        {
            BrokerSubmitResult result = await broker.SubmitAsync(
                DiagnosticCommandFactory.EmergencyFlatten(record, instrumentSlot, clock.UtcNow),
                cancellationToken);
            if (result.State == BrokerSubmitState.Rejected)
                return Fail(record, result.ReasonCode ?? "EMERGENCY_REJECTED");
            if (result.State != BrokerSubmitState.Acknowledged || string.IsNullOrWhiteSpace(result.BrokerOrderId))
                return await RecoverAsync(record, cancellationToken);

            store.Update(record.ExperimentId, current => current with
            {
                State = "EmergencyFlattenAccepted",
                EmergencyBrokerOrderId = result.BrokerOrderId,
                Failure = DiagnosticExecutionFailure.None,
                FailureReason = null
            });
            return DiagnosticEmergencyFlattenResult.Working;
        }
        catch (Exception exception)
            when (DiagnosticFailureClassification.IsAmbiguousSubmission(exception, cancellationToken))
        {
            return await RecoverAsync(record, cancellationToken);
        }
    }

    /// <summary>
    /// Resolves a submission whose outcome is unknown by asking the venue for the deterministic client
    /// order ID. An order that cannot be found did not reach the venue under that ID, and the single-shot
    /// claim has already been spent, so the attempt fails rather than sending again.
    /// </summary>
    private async Task<DiagnosticEmergencyFlattenResult> RecoverAsync(
        DiagnosticExecutionRecord record,
        CancellationToken cancellationToken)
    {
        BrokerOrderSnapshot? existing = await broker.FindByClientOrderIdAsync(
            record.EmergencyClientOrderId!,
            cancellationToken);
        return existing is null
            ? Fail(record, "EMERGENCY_SUBMISSION_UNKNOWN")
            : Track(record, existing);
    }

    /// <summary>Records what the venue says about the flatten order and reports what remains to be done.</summary>
    private DiagnosticEmergencyFlattenResult Track(DiagnosticExecutionRecord record, BrokerOrderSnapshot order)
    {
        string status = order.Status.Trim().ToLowerInvariant();
        if (status is "rejected" or "canceled" or "expired")
            return Fail(record, $"EMERGENCY_{status.ToUpperInvariant()}");

        bool filled = status == "filled";
        store.Update(record.ExperimentId, current => current with
        {
            State = filled ? "Reconciling" : "EmergencyFlattenAccepted",
            EmergencyBrokerOrderId = order.BrokerOrderId,
            EmergencyFilledQuantity = order.FilledQuantity,
            Failure = DiagnosticExecutionFailure.None,
            FailureReason = null
        });
        return filled ? DiagnosticEmergencyFlattenResult.Flat : DiagnosticEmergencyFlattenResult.Working;
    }

    private DiagnosticEmergencyFlattenResult Fail(DiagnosticExecutionRecord record, string reason)
    {
        store.Update(record.ExperimentId, current => current with
        {
            State = "EmergencyFlattenFailed",
            Failure = DiagnosticExecutionFailure.EmergencyFlattenFailed,
            FailureReason = reason
        });
        return DiagnosticEmergencyFlattenResult.Failed(reason);
    }
}
