using QuantDesk.Api.PaperTrading;
using QuantDesk.Runtime.Persistence;

namespace QuantDesk.Api.Tests;

/// <summary>
/// Exposure and profit arithmetic extracted from a 1,072-line service. A lane that miscounts its
/// own exposure stops closing it, so these are worth pinning precisely.
/// </summary>
public sealed class DiagnosticExecutionMathTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BeforeAnExitIsSizedExposureIsWhatTheEntryFilled()
    {
        DiagnosticExecutionRecord record = Record() with { EntryFilledQuantity = 0.5m };

        Assert.Equal(0.5m, DiagnosticExecutionMath.InternalExposure(record));
    }

    [Fact]
    public void OnceAnExitIsSizedItBecomesTheBasisNotTheEntryFill()
    {
        // Using the entry fill after a partial exit would understate what remains, and a lane that
        // understates its exposure stops closing it.
        DiagnosticExecutionRecord record = Record() with
        {
            EntryFilledQuantity = 1.0m,
            ExitQuantity = 0.6m,
            ExitFilledQuantity = 0.2m
        };

        Assert.Equal(0.4m, DiagnosticExecutionMath.InternalExposure(record));
    }

    [Fact]
    public void AnEmergencyFlattenReducesExposureAlongsideTheManagedExit()
    {
        DiagnosticExecutionRecord record = Record() with
        {
            EntryFilledQuantity = 1.0m,
            ExitFilledQuantity = 0.3m,
            EmergencyFilledQuantity = 0.7m
        };

        Assert.Equal(0m, DiagnosticExecutionMath.InternalExposure(record));
    }

    [Fact]
    public void AnOverFillCannotPresentAsNegativeExposure()
    {
        DiagnosticExecutionRecord record = Record() with
        {
            EntryFilledQuantity = 1.0m,
            ExitFilledQuantity = 1.5m
        };

        Assert.Equal(0m, DiagnosticExecutionMath.InternalExposure(record));
    }

    [Fact]
    public void ProfitIsRealisedOnlyOnACompletedRoundTrip()
    {
        DiagnosticExecutionRecord record = Record() with
        {
            EntryAverageFillPrice = 100m,
            ExitAverageFillPrice = 110m,
            ExitFilledQuantity = 0.5m
        };

        Assert.Equal(5m, DiagnosticExecutionMath.GrossPaperPnl(record));
    }

    [Fact]
    public void AnIncompleteRoundTripReportsNullRatherThanZero()
    {
        // Zero would be indistinguishable from a genuine break-even.
        Assert.Null(DiagnosticExecutionMath.GrossPaperPnl(Record()));
        Assert.Null(DiagnosticExecutionMath.GrossPaperPnl(
            Record() with { EntryAverageFillPrice = 100m, ExitFilledQuantity = 0.5m }));
        Assert.Null(DiagnosticExecutionMath.GrossPaperPnl(
            Record() with { EntryAverageFillPrice = 100m, ExitAverageFillPrice = 110m }));
    }

    [Fact]
    public void ALosingRoundTripReportsANegativeResult()
    {
        DiagnosticExecutionRecord record = Record() with
        {
            EntryAverageFillPrice = 110m,
            ExitAverageFillPrice = 100m,
            ExitFilledQuantity = 0.5m
        };

        Assert.Equal(-5m, DiagnosticExecutionMath.GrossPaperPnl(record));
    }

    [Fact]
    public void TheMostActionableReconciliationReasonIsReportedFirst()
    {
        // An unresolved order explains a mismatch, so it must be named ahead of the mismatch.
        Assert.Equal(
            "RECONCILIATION_UNRESOLVED_DIAGNOSTIC_ORDERS",
            DiagnosticExecutionMath.ReconciliationFailureReason(true, 1m, 0m));
        Assert.Equal(
            "RECONCILIATION_BROKER_INTERNAL_MISMATCH",
            DiagnosticExecutionMath.ReconciliationFailureReason(false, 1m, 0m));
        Assert.Equal(
            "RECONCILIATION_BROKER_EXPOSURE_REMAINS",
            DiagnosticExecutionMath.ReconciliationFailureReason(false, 1m, 1m));
        Assert.Equal(
            "RECONCILIATION_INTERNAL_EXPOSURE_REMAINS",
            DiagnosticExecutionMath.ReconciliationFailureReason(false, 0m, 0m));
    }

    [Theory]
    [InlineData("EntryCanceled")]
    [InlineData("EntryRejected")]
    [InlineData("EntryExpired")]
    [InlineData("ReconciliationFailed")]
    [InlineData("EmergencyFlattenFailed")]
    public void TerminalEntryStatesAreRecognised(string state) =>
        Assert.True(DiagnosticExecutionMath.IsTerminalEntryState(state));

    [Theory]
    [InlineData("EntryFilled")]
    [InlineData("Holding")]
    public void OngoingEntryStatesAreNotTerminal(string state) =>
        Assert.False(DiagnosticExecutionMath.IsTerminalEntryState(state));

    [Fact]
    public void ExitLifecycleStatesCoverEveryExitOutcome()
    {
        foreach (string state in new[]
        {
            "ExitDue", "ExitReserved", "ExitSubmitted", "ExitAccepted", "ExitPartiallyFilled",
            "ExitFilled", "ExitSubmissionUnknown", "ExitCanceled", "ExitRejected", "ExitExpired"
        })
            Assert.True(DiagnosticExecutionMath.IsExitLifecycleState(state), state);

        Assert.False(DiagnosticExecutionMath.IsExitLifecycleState("EntryFilled"));
    }

    [Fact]
    public void AnEndedExitOrderIsTerminalForTheOrderButNotNecessarilyForTheLane()
    {
        // The position may still be open, so the caller must re-derive exposure rather than stop.
        Assert.True(DiagnosticExecutionMath.IsTerminalExitState("ExitCanceled"));
        Assert.False(DiagnosticExecutionMath.IsTerminalExitState("ExitFilled"));
    }

    private static DiagnosticExecutionRecord Record() =>
        new("EXP-1", "diagnostic", "BTC/USD", "EntryReserved",
            RequestedNotional: 10m, HoldingDuration: TimeSpan.FromMinutes(2), CreatedAt: Now,
            EntryClientOrderId: "entry-id", ExitClientOrderId: "exit-id");
}
