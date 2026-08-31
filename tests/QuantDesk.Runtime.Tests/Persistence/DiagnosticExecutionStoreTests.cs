using QuantDesk.Runtime.Persistence;

namespace QuantDesk.Runtime.Tests.Persistence;

public sealed class DiagnosticExecutionStoreTests
{
    [Fact]
    public void Full_record_persistence_round_trip_preserves_every_stage_one_field()
    {
        string path = TemporaryPath();
        try
        {
            DiagnosticExecutionRecord expected = CompleteRecord();
            new DiagnosticExecutionStore(path).Record(expected);
            DiagnosticExecutionRecord actual = new DiagnosticExecutionStore(path).Find(expected.ExperimentId)!;
            Assert.Equal(expected, actual);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void State_survives_store_reconstruction()
    {
        string path = TemporaryPath();
        try
        {
            DiagnosticExecutionRecord expected = CompleteRecord() with { State = "Holding" };
            new DiagnosticExecutionStore(path).Record(expected);
            Assert.Equal("Holding", new DiagnosticExecutionStore(path).Find(expected.ExperimentId)!.State);
        }
        finally { Delete(path); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Deterministic_leg_id_survives_restart(bool entryLeg)
    {
        string path = TemporaryPath();
        try
        {
            DiagnosticExecutionRecord expected = CompleteRecord();
            new DiagnosticExecutionStore(path).Record(expected);
            DiagnosticExecutionRecord actual = new DiagnosticExecutionStore(path).Find(expected.ExperimentId)!;
            Assert.Equal(entryLeg ? expected.EntryClientOrderId : expected.ExitClientOrderId,
                entryLeg ? actual.EntryClientOrderId : actual.ExitClientOrderId);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void Duplicate_entry_reservation_is_prevented()
    {
        string path = TemporaryPath();
        try
        {
            var store = new DiagnosticExecutionStore(path);
            DiagnosticExecutionRecord record = CompleteRecord() with { State = "EntryReserved" };
            Assert.True(store.TryCreateReservation(record, record.EntryClientOrderId!, record.ExitClientOrderId!));
            Assert.False(store.TryCreateReservation(record, record.EntryClientOrderId!, record.ExitClientOrderId!));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void Duplicate_exit_reservation_is_prevented()
    {
        string path = TemporaryPath();
        try
        {
            var store = new DiagnosticExecutionStore(path);
            DiagnosticExecutionRecord record = CompleteRecord() with { State = "ExitDue", ExitReservedAt = null };
            store.Record(record);
            Assert.True(store.TryReserveExit(record.ExperimentId, 0.00005m, Timestamp(11), out _));
            Assert.False(store.TryReserveExit(record.ExperimentId, 0.00005m, Timestamp(12), out _));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void Duplicate_client_order_id_is_prevented_across_experiments()
    {
        string path = TemporaryPath();
        try
        {
            var store = new DiagnosticExecutionStore(path);
            DiagnosticExecutionRecord first = CompleteRecord();
            DiagnosticExecutionRecord second = first with { ExperimentId = "CRYPTO-DIAGNOSTIC-2026-08-31-002" };
            Assert.True(store.TryCreateReservation(first, first.EntryClientOrderId!, first.ExitClientOrderId!));
            Assert.False(store.TryCreateReservation(second, first.EntryClientOrderId!, "different-exit-id"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void Persisted_final_entry_fill_schedules_exit_exactly_two_minutes_later()
    {
        string path = TemporaryPath();
        try
        {
            DateTimeOffset finalFill = Timestamp(20);
            DiagnosticExecutionRecord record = CompleteRecord() with
            {
                State = "EntryFilled", FinalEntryFillAt = finalFill, HoldStartedAt = finalFill,
                ScheduledExitAt = finalFill.AddMinutes(2)
            };
            new DiagnosticExecutionStore(path).Record(record);
            DiagnosticExecutionRecord restarted = new DiagnosticExecutionStore(path).Find(record.ExperimentId)!;
            Assert.Equal(restarted.FinalEntryFillAt!.Value.Add(TimeSpan.FromMinutes(2)), restarted.ScheduledExitAt);
        }
        finally { Delete(path); }
    }

    private static DiagnosticExecutionRecord CompleteRecord()
    {
        DateTimeOffset entryFill = Timestamp(5);
        DateTimeOffset exitFill = Timestamp(10);
        return new DiagnosticExecutionRecord(
            "CRYPTO-DIAGNOSTIC-2026-08-31-001", "DiagnosticExecution", "BTC/USD", "Complete", 5m,
            TimeSpan.FromMinutes(2), Timestamp(0),
            "qd-diag-4eba8f5a8b0b-entry", "qd-diag-4eba8f5a8b0b-exit")
        {
            ExecutionMode = "PAPER", RequestedQuantity = 0.00005m,
            EntryBrokerOrderId = "entry-broker-id", EntryReservedAt = Timestamp(1),
            EntrySubmissionAttemptedAt = Timestamp(2), EntrySubmittedAt = Timestamp(3),
            EntryAcknowledgedAt = Timestamp(4), FirstEntryFillAt = Timestamp(4),
            FinalEntryFillAt = entryFill, EntryFilledQuantity = 0.00005m,
            EntryAverageFillPrice = 100_000m, EntryReferencePrice = 99_990m,
            HoldStartedAt = entryFill, ScheduledExitAt = entryFill.AddMinutes(2),
            ExitBrokerOrderId = "exit-broker-id", ExitReservedAt = Timestamp(6),
            ExitSubmissionAttemptedAt = Timestamp(7), ExitSubmittedAt = Timestamp(8),
            ExitAcknowledgedAt = Timestamp(9), FirstExitFillAt = Timestamp(9),
            FinalExitFillAt = exitFill, ExitQuantity = 0.00005m, ExitFilledQuantity = 0.00005m,
            ExitAverageFillPrice = 100_100m, ExitReferencePrice = 100_090m,
            FinalBrokerQuantity = 0m, FinalInternalQuantity = 0m, ReconciliationResult = "Flat",
            GrossPaperPnl = 0.005m, CompletedAt = Timestamp(11)
        };
    }

    private static DateTimeOffset Timestamp(int seconds) =>
        new(2026, 8, 31, 10, 0, seconds, TimeSpan.Zero);

    private static string TemporaryPath() =>
        Path.Combine(Path.GetTempPath(), $"qd-{Guid.NewGuid():N}.json");

    private static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
    }
}
