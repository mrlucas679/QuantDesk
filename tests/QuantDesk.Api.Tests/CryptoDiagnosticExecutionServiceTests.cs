using QuantDesk.Alpaca.Mapping;
using QuantDesk.Alpaca.MarketData;
using QuantDesk.Api.PaperTrading;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Persistence;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.Tests;

public sealed class CryptoDiagnosticExecutionServiceTests
{
    [Fact]
    public async Task Reservation_is_durable_before_broker_submission()
    {
        using var fixture = new DiagnosticFixture();
        bool durableReservationObserved = false;
        fixture.Broker.SubmitBehavior = command =>
        {
            var restarted = new DiagnosticExecutionStore(fixture.StorePath);
            DiagnosticExecutionRecord persisted = restarted.Find("reservation-proof")!;
            durableReservationObserved = persisted.State == "EntryReserved" &&
                persisted.EntryReservedAt is not null &&
                persisted.EntrySubmissionAttemptedAt is not null &&
                command.ClientOrderId == persisted.EntryClientOrderId;
            return Task.FromResult(Acknowledged("broker-reservation"));
        };

        await fixture.PrepareAsync("reservation-proof");
        await fixture.Service.AdvanceAsync("reservation-proof", 0.00001m, CancellationToken.None);

        Assert.True(durableReservationObserved);
        Assert.Equal(1, fixture.Broker.SubmitCount);
    }

    [Fact]
    public async Task Repeated_advance_calls_submit_at_most_one_entry_post()
    {
        using var fixture = new DiagnosticFixture();
        await fixture.PrepareAsync("single-post");

        await fixture.Service.AdvanceAsync("single-post", 0.00001m, CancellationToken.None);
        await fixture.Service.AdvanceAsync("single-post", 0.00001m, CancellationToken.None);
        await fixture.Service.AdvanceAsync("single-post", 0.00001m, CancellationToken.None);

        Assert.Equal(1, fixture.Broker.SubmitCount);
        Assert.Equal("EntryAccepted", fixture.Store.Find("single-post")!.State);
    }

    [Fact]
    public async Task Timeout_after_broker_acceptance_recovers_order_by_same_client_id()
    {
        using var fixture = new DiagnosticFixture();
        await fixture.PrepareAsync("timeout-recovery");
        fixture.Broker.SubmitBehavior = command =>
        {
            fixture.Broker.LookupBehavior = requestedId => requestedId == command.ClientOrderId
                ? Order("broker-existing", requestedId, "accepted", 0, null)
                : null;
            return Task.FromException<BrokerSubmitResult>(new TimeoutException("simulated response timeout"));
        };

        DiagnosticExecutionResult result = await fixture.Service.AdvanceAsync(
            "timeout-recovery", 0.00001m, CancellationToken.None);

        DiagnosticExecutionRecord record = fixture.Store.Find("timeout-recovery")!;
        Assert.True(result.Allowed);
        Assert.Equal(1, fixture.Broker.SubmitCount);
        Assert.Equal("broker-existing", record.EntryBrokerOrderId);
        Assert.Equal("EntryAccepted", record.State);
        Assert.Equal(record.EntryClientOrderId, fixture.Broker.LastSubmittedCommand!.ClientOrderId);
    }

    [Fact]
    public async Task Accepted_partial_and_filled_updates_persist_broker_truth()
    {
        using var fixture = new DiagnosticFixture();
        await fixture.PrepareAsync("fill-lifecycle");
        DateTimeOffset createdAt = DateTimeOffset.Parse("2026-08-30T10:00:00Z");
        DateTimeOffset submittedAt = createdAt.AddSeconds(1);
        DateTimeOffset partialAt = createdAt.AddSeconds(2);
        DateTimeOffset filledAt = createdAt.AddSeconds(3);
        int lookup = 0;
        fixture.Broker.LookupBehavior = clientOrderId => ++lookup switch
        {
            1 => null,
            2 => SetBrokerTruth(
                fixture.Broker,
                Order("broker-fill", clientOrderId, "partially_filled", 0.00002m, 100_000m) with
                {
                    CreatedAt = createdAt,
                    SubmittedAt = submittedAt,
                    UpdatedAt = partialAt
                },
                0.00002m),
            _ => SetBrokerTruth(
                fixture.Broker,
                Order("broker-fill", clientOrderId, "filled", 0.00005m, 100_100m) with
                {
                    CreatedAt = createdAt,
                    SubmittedAt = submittedAt,
                    UpdatedAt = filledAt,
                    FilledAt = filledAt
                },
                0.00005m)
        };

        await fixture.Service.AdvanceAsync("fill-lifecycle", 0.00005m, CancellationToken.None);
        Assert.Equal("EntryAccepted", fixture.Store.Find("fill-lifecycle")!.State);

        await fixture.Service.AdvanceAsync("fill-lifecycle", 0.00005m, CancellationToken.None);
        DiagnosticExecutionRecord partial = fixture.Store.Find("fill-lifecycle")!;
        Assert.Equal("EntryPartiallyFilled", partial.State);
        Assert.Equal(0.00002m, partial.EntryFilledQuantity);
        Assert.Equal(100_000m, partial.EntryAverageFillPrice);
        Assert.Equal(partialAt, partial.FirstEntryFillAt);

        await fixture.Service.AdvanceAsync("fill-lifecycle", 0.00005m, CancellationToken.None);
        DiagnosticExecutionRecord filled = fixture.Store.Find("fill-lifecycle")!;
        Assert.Equal("Holding", filled.State);
        Assert.Equal("broker-fill", filled.EntryBrokerOrderId);
        Assert.Equal(0.00005m, filled.EntryFilledQuantity);
        Assert.Equal(100_100m, filled.EntryAverageFillPrice);
        Assert.Equal(createdAt, filled.EntryBrokerCreatedAt);
        Assert.Equal(submittedAt, filled.EntrySubmittedAt);
        Assert.Equal(filledAt, filled.EntryBrokerUpdatedAt);
        Assert.Equal(filledAt, filled.FinalEntryFillAt);
        Assert.Equal(filledAt, filled.HoldStartedAt);
        Assert.Equal(filledAt.AddMinutes(2), filled.ScheduledExitAt);
    }

    [Fact]
    public async Task Known_filled_entry_is_attached_before_fee_adjusted_position_reconciliation()
    {
        using var fixture = new DiagnosticFixture();
        await fixture.PrepareAsync("fee-adjusted-entry");
        await fixture.Service.AdvanceAsync(
            "fee-adjusted-entry", 0.0001m, CancellationToken.None);
        DateTimeOffset filledAt = fixture.Clock.UtcNow.AddSeconds(1);
        string clientOrderId = fixture.Store.Find("fee-adjusted-entry")!.EntryClientOrderId!;
        fixture.Broker.LookupBehavior = requestedId => requestedId == clientOrderId
            ? Order("broker-fee-adjusted", requestedId, "filled", 0.0001m, 100_000m) with
            {
                FilledAt = filledAt,
                UpdatedAt = filledAt
            }
            : null;
        fixture.Broker.Positions =
        [
            new BrokerPositionSnapshot(
                DiagnosticExecutionOptions.RequiredSymbol,
                0,
                0.00009975m,
                100_000m)
        ];

        DiagnosticExecutionResult result = await fixture.Service.AdvanceAsync(
            "fee-adjusted-entry", 0.0001m, CancellationToken.None);

        DiagnosticExecutionRecord persisted = fixture.Store.Find("fee-adjusted-entry")!;
        Assert.True(result.Allowed, result.Reason);
        Assert.Equal("Holding", persisted.State);
        Assert.Equal("broker-fee-adjusted", persisted.EntryBrokerOrderId);
        Assert.Equal(0.0001m, persisted.EntryFilledQuantity);
        Assert.Equal(DiagnosticExecutionFailure.None, persisted.Failure);
        Assert.Null(persisted.FailureReason);
    }

    [Fact]
    public async Task Filled_entry_schedules_exit_exactly_two_minutes_after_final_fill()
    {
        using var fixture = new DiagnosticFixture();
        DateTimeOffset filledAt = fixture.Clock.UtcNow;

        DiagnosticExecutionRecord holding = await EnterHoldingAsync(
            fixture, "exact-hold", filledAt, 0.00005m);

        Assert.Equal(filledAt, holding.HoldStartedAt);
        Assert.Equal(filledAt.Add(TimeSpan.FromMinutes(2)), holding.ScheduledExitAt);
        Assert.Equal(TimeSpan.FromMinutes(2), holding.ScheduledExitAt - holding.HoldStartedAt);
    }

    [Fact]
    public async Task Restart_during_hold_retains_identical_scheduled_exit_time()
    {
        using var fixture = new DiagnosticFixture();
        DiagnosticExecutionRecord holding = await EnterHoldingAsync(
            fixture, "restart-hold", fixture.Clock.UtcNow, 0.00005m);
        DateTimeOffset scheduledExitAt = holding.ScheduledExitAt!.Value;
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        var restartedStore = new DiagnosticExecutionStore(fixture.StorePath);
        CryptoDiagnosticExecutionService restarted = fixture.CreateService(restartedStore);

        await restarted.AdvanceAsync("restart-hold", 0, CancellationToken.None);

        DiagnosticExecutionRecord persisted = restartedStore.Find("restart-hold")!;
        Assert.Equal("Holding", persisted.State);
        Assert.Equal(scheduledExitAt, persisted.ScheduledExitAt);
    }

    [Fact]
    public async Task Overdue_hold_progresses_durably_to_exit_due()
    {
        using var fixture = new DiagnosticFixture();
        await EnterHoldingAsync(fixture, "overdue-hold", fixture.Clock.UtcNow, 0.00005m);
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));

        await fixture.Service.AdvanceAsync("overdue-hold", 0, CancellationToken.None);

        Assert.Equal("ExitDue", fixture.Store.Find("overdue-hold")!.State);
        Assert.Equal(0, fixture.Broker.ExitSubmitCount);
    }

    [Fact]
    public async Task Repeated_worker_execution_submits_at_most_one_exit_post_after_durable_reservation()
    {
        using var fixture = new DiagnosticFixture();
        await ProgressToExitDueAsync(fixture, "single-exit-post", 0.00005m);
        bool durableExitReservationObserved = false;
        fixture.Broker.SubmitBehavior = command =>
        {
            var restarted = new DiagnosticExecutionStore(fixture.StorePath);
            DiagnosticExecutionRecord persisted = restarted.Find("single-exit-post")!;
            durableExitReservationObserved = command.Side == OrderSide.Sell &&
                persisted.State == "ExitReserved" &&
                persisted.ExitReservedAt is not null &&
                persisted.ExitSubmissionAttemptedAt is not null &&
                persisted.ExitQuantity == 0.00005m;
            return Task.FromResult(Acknowledged("broker-exit"));
        };

        await fixture.Service.AdvanceAsync("single-exit-post", 0, CancellationToken.None);
        await fixture.Service.AdvanceAsync("single-exit-post", 0, CancellationToken.None);
        await fixture.Service.AdvanceAsync("single-exit-post", 0, CancellationToken.None);

        Assert.True(durableExitReservationObserved);
        Assert.Equal(1, fixture.Broker.ExitSubmitCount);
        Assert.Equal("ExitAccepted", fixture.Store.Find("single-exit-post")!.State);
    }

    [Fact]
    public async Task Ambiguous_exit_submission_recovers_existing_deterministic_order()
    {
        using var fixture = new DiagnosticFixture();
        await ProgressToExitDueAsync(fixture, "exit-timeout", 0.00005m);
        fixture.Broker.LookupBehavior = _ => null;
        fixture.Broker.SubmitBehavior = command =>
        {
            if (command.Side == OrderSide.Sell)
            {
                fixture.Broker.LookupBehavior = requestedId => requestedId == command.ClientOrderId
                    ? Order("broker-existing-exit", requestedId, "accepted", 0, null)
                    : null;
                return Task.FromException<BrokerSubmitResult>(new TimeoutException("simulated exit timeout"));
            }
            return Task.FromResult(Acknowledged("broker-entry"));
        };

        DiagnosticExecutionResult result = await fixture.Service.AdvanceAsync(
            "exit-timeout", 0, CancellationToken.None);

        DiagnosticExecutionRecord record = fixture.Store.Find("exit-timeout")!;
        Assert.True(result.Allowed);
        Assert.Equal(1, fixture.Broker.ExitSubmitCount);
        Assert.Equal("ExitAccepted", record.State);
        Assert.Equal("broker-existing-exit", record.ExitBrokerOrderId);
        Assert.Equal(record.ExitClientOrderId, fixture.Broker.LastSubmittedCommand!.ClientOrderId);
    }

    [Fact]
    public async Task Exit_accepted_partial_and_filled_updates_persist_broker_truth()
    {
        using var fixture = new DiagnosticFixture();
        const decimal entryQuantity = 0.00005m;
        await ProgressToExitDueAsync(fixture, "exit-fill-lifecycle", entryQuantity);
        await fixture.Service.AdvanceAsync("exit-fill-lifecycle", 0, CancellationToken.None);
        DiagnosticExecutionRecord accepted = fixture.Store.Find("exit-fill-lifecycle")!;
        Assert.Equal("ExitAccepted", accepted.State);
        Assert.Equal(entryQuantity, accepted.ExitQuantity);

        DateTimeOffset createdAt = fixture.Clock.UtcNow.AddSeconds(1);
        DateTimeOffset partialAt = createdAt.AddSeconds(1);
        DateTimeOffset filledAt = createdAt.AddSeconds(2);
        fixture.Broker.LookupBehavior = clientOrderId => SetExitBrokerTruth(
            fixture.Broker,
            Order("broker-exit-fill", clientOrderId, "partially_filled", 0.00002m, 100_200m) with
            {
                CreatedAt = createdAt,
                SubmittedAt = createdAt,
                UpdatedAt = partialAt
            },
            remainingPosition: 0.00003m);

        await fixture.Service.AdvanceAsync("exit-fill-lifecycle", 0, CancellationToken.None);
        DiagnosticExecutionRecord partial = fixture.Store.Find("exit-fill-lifecycle")!;
        Assert.Equal("ExitPartiallyFilled", partial.State);
        Assert.Equal(0.00002m, partial.ExitFilledQuantity);
        Assert.Equal(100_200m, partial.ExitAverageFillPrice);
        Assert.Equal(partialAt, partial.FirstExitFillAt);

        fixture.Broker.LookupBehavior = clientOrderId => SetExitBrokerTruth(
            fixture.Broker,
            Order("broker-exit-fill", clientOrderId, "filled", entryQuantity, 100_250m) with
            {
                CreatedAt = createdAt,
                SubmittedAt = createdAt,
                UpdatedAt = filledAt,
                FilledAt = filledAt
            },
            remainingPosition: 0);

        await fixture.Service.AdvanceAsync("exit-fill-lifecycle", 0, CancellationToken.None);
        DiagnosticExecutionRecord completed = fixture.Store.Find("exit-fill-lifecycle")!;
        Assert.Equal("Complete", completed.State);
        Assert.Equal("broker-exit-fill", completed.ExitBrokerOrderId);
        Assert.Equal(entryQuantity, completed.ExitFilledQuantity);
        Assert.Equal(100_250m, completed.ExitAverageFillPrice);
        Assert.Equal(filledAt, completed.FinalExitFillAt);
        Assert.NotNull(completed.CompletedAt);
        Assert.Equal(0, completed.FinalBrokerQuantity);
        Assert.Equal(0, completed.FinalInternalQuantity);
        Assert.Equal("Flat", completed.ReconciliationResult);
        Assert.Equal(0.0125m, completed.GrossPaperPnl);
    }

    [Theory]
    [InlineData("EntryReserved", "accepted", "EntryAccepted")]
    [InlineData("EntrySubmitted", "accepted", "EntryAccepted")]
    [InlineData("EntryAccepted", "partially_filled", "EntryPartiallyFilled")]
    [InlineData("EntryPartiallyFilled", "filled", "Holding")]
    public async Task Restart_resumes_each_entry_tracking_phase(
        string persistedState,
        string brokerStatus,
        string expectedState)
    {
        using var fixture = new DiagnosticFixture();
        await fixture.PrepareAsync($"restart-{persistedState}");
        DiagnosticExecutionRecord initial = fixture.Store.Find($"restart-{persistedState}")!;
        decimal filledQuantity = brokerStatus == "partially_filled" ? 0.00002m :
            brokerStatus == "filled" ? 0.00005m : 0;
        fixture.Store.Update(initial.ExperimentId, current => current with
        {
            State = persistedState,
            RequestedQuantity = 0.00005m,
            EntrySubmissionAttemptedAt = persistedState == "EntryReserved" ? null : fixture.Clock.UtcNow
        });
        BrokerOrderSnapshot order = Order(
            "recovered-entry",
            initial.EntryClientOrderId!,
            brokerStatus,
            filledQuantity,
            filledQuantity > 0 ? 100_000m : null) with
        {
            FilledAt = brokerStatus == "filled" ? fixture.Clock.UtcNow : null,
            UpdatedAt = fixture.Clock.UtcNow
        };
        fixture.Broker.LookupBehavior = id => id == initial.EntryClientOrderId
            ? SetBrokerTruth(fixture.Broker, order, filledQuantity)
            : null;
        CryptoDiagnosticExecutionService restarted = fixture.CreateService(
            new DiagnosticExecutionStore(fixture.StorePath));

        await restarted.AdvanceAsync(initial.ExperimentId, 0.00005m, CancellationToken.None);

        Assert.Equal(expectedState, fixture.Store.Find(initial.ExperimentId)!.State);
        Assert.Equal(0, fixture.Broker.SubmitCount);
    }

    [Theory]
    [InlineData("ExitReserved", "accepted", "ExitAccepted")]
    [InlineData("ExitSubmitted", "accepted", "ExitAccepted")]
    [InlineData("ExitAccepted", "partially_filled", "ExitPartiallyFilled")]
    [InlineData("ExitPartiallyFilled", "filled", "Complete")]
    public async Task Restart_resumes_each_exit_tracking_phase_without_duplicate_post(
        string persistedState,
        string brokerStatus,
        string expectedState)
    {
        using var fixture = new DiagnosticFixture();
        const decimal quantity = 0.00005m;
        await ProgressToExitDueAsync(fixture, $"restart-{persistedState}", quantity);
        DiagnosticExecutionRecord initial = fixture.Store.Find($"restart-{persistedState}")!;
        fixture.Store.Update(initial.ExperimentId, current => current with
        {
            State = persistedState,
            ExitQuantity = quantity,
            ExitReservedAt = fixture.Clock.UtcNow,
            ExitSubmissionAttemptedAt = fixture.Clock.UtcNow
        });
        decimal filledQuantity = brokerStatus == "partially_filled" ? 0.00002m :
            brokerStatus == "filled" ? quantity : 0;
        decimal remaining = quantity - filledQuantity;
        BrokerOrderSnapshot order = Order(
            "recovered-exit",
            initial.ExitClientOrderId!,
            brokerStatus,
            filledQuantity,
            filledQuantity > 0 ? 100_000m : null) with
        {
            FilledAt = brokerStatus == "filled" ? fixture.Clock.UtcNow : null,
            UpdatedAt = fixture.Clock.UtcNow
        };
        fixture.Broker.LookupBehavior = id => id == initial.ExitClientOrderId
            ? SetExitBrokerTruth(fixture.Broker, order, remaining)
            : null;
        CryptoDiagnosticExecutionService restarted = fixture.CreateService(
            new DiagnosticExecutionStore(fixture.StorePath));

        await restarted.AdvanceAsync(initial.ExperimentId, 0, CancellationToken.None);

        Assert.Equal(expectedState, fixture.Store.Find(initial.ExperimentId)!.State);
        Assert.Equal(0, fixture.Broker.ExitSubmitCount);
    }

    [Fact]
    public async Task Restart_in_reconciling_resumes_and_completes_only_at_zero_zero()
    {
        using var fixture = new DiagnosticFixture();
        const string experimentId = "restart-reconciling";
        await ProgressToExitDueAsync(fixture, experimentId, 0.00005m);
        fixture.Store.Update(experimentId, current => current with
        {
            State = "Reconciling",
            EntryFilledQuantity = 0.00005m,
            ExitFilledQuantity = 0.00005m
        });
        fixture.Broker.OpenOrders = [];
        fixture.Broker.Positions = [];
        CryptoDiagnosticExecutionService restarted = fixture.CreateService(
            new DiagnosticExecutionStore(fixture.StorePath));

        await restarted.AdvanceAsync(experimentId, 0, CancellationToken.None);

        DiagnosticExecutionRecord completed = fixture.Store.Find(experimentId)!;
        Assert.Equal("Complete", completed.State);
        Assert.Equal(0, completed.FinalBrokerQuantity);
        Assert.Equal(0, completed.FinalInternalQuantity);
    }

    [Fact]
    public async Task Broker_internal_mismatch_enters_typed_reconciliation_failure()
    {
        using var fixture = new DiagnosticFixture();
        const string experimentId = "reconciliation-mismatch";
        await ProgressToExitDueAsync(fixture, experimentId, 0.00005m);
        fixture.Store.Update(experimentId, current => current with
        {
            State = "Reconciling",
            EntryFilledQuantity = 0.00005m,
            ExitFilledQuantity = 0.00005m
        });
        fixture.Broker.OpenOrders = [];
        fixture.Broker.Positions =
        [
            new BrokerPositionSnapshot(DiagnosticExecutionOptions.RequiredSymbol, 0, 0.00001m, 100_000m)
        ];

        DiagnosticExecutionResult result = await fixture.Service.AdvanceAsync(
            experimentId, 0, CancellationToken.None);

        DiagnosticExecutionRecord failed = fixture.Store.Find(experimentId)!;
        Assert.False(result.Allowed);
        Assert.Equal("ReconciliationFailed", failed.State);
        Assert.Equal(DiagnosticExecutionFailure.ReconciliationFailed, failed.Failure);
        Assert.Equal("RECONCILIATION_BROKER_INTERNAL_MISMATCH", failed.FailureReason);
    }

    [Fact]
    public async Task Emergency_flatten_is_idempotent_and_verifies_flat_after_fill()
    {
        using var fixture = new DiagnosticFixture();
        const string experimentId = "emergency-idempotent";
        const decimal quantity = 0.00005m;
        await EnterHoldingAsync(fixture, experimentId, fixture.Clock.UtcNow, quantity);
        DiagnosticExecutionRecord initial = fixture.Store.Find(experimentId)!;
        fixture.Broker.LookupBehavior = _ => null;

        await fixture.Service.EmergencyFlattenAsync(experimentId, CancellationToken.None);
        Assert.Equal(1, fixture.Broker.EmergencySubmitCount);
        BrokerOrderSnapshot filled = Order(
            "emergency-broker",
            initial.EmergencyClientOrderId!,
            "filled",
            quantity,
            99_900m) with
        { FilledAt = fixture.Clock.UtcNow };
        fixture.Broker.LookupBehavior = id => id == initial.EmergencyClientOrderId
            ? SetExitBrokerTruth(fixture.Broker, filled, 0)
            : null;
        CryptoDiagnosticExecutionService restarted = fixture.CreateService(
            new DiagnosticExecutionStore(fixture.StorePath));

        await restarted.EmergencyFlattenAsync(experimentId, CancellationToken.None);
        await restarted.EmergencyFlattenAsync(experimentId, CancellationToken.None);

        Assert.Equal(1, fixture.Broker.EmergencySubmitCount);
        Assert.Equal("Complete", fixture.Store.Find(experimentId)!.State);
    }

    [Fact]
    public async Task Emergency_flatten_claim_prevents_duplicate_post_across_restart()
    {
        using var fixture = new DiagnosticFixture();
        const string experimentId = "emergency-restart-dedup";
        await EnterHoldingAsync(fixture, experimentId, fixture.Clock.UtcNow, 0.00005m);
        fixture.Broker.LookupBehavior = _ => null;

        await fixture.Service.EmergencyFlattenAsync(experimentId, CancellationToken.None);
        CryptoDiagnosticExecutionService restarted = fixture.CreateService(
            new DiagnosticExecutionStore(fixture.StorePath));
        await restarted.EmergencyFlattenAsync(experimentId, CancellationToken.None);

        Assert.Equal(1, fixture.Broker.EmergencySubmitCount);
        Assert.Equal("EmergencyFlattenFailed", fixture.Store.Find(experimentId)!.State);
    }

    [Fact]
    public async Task Emergency_flatten_cancels_only_unresolved_diagnostic_orders_before_post()
    {
        using var fixture = new DiagnosticFixture();
        const string experimentId = "emergency-cancel";
        await EnterHoldingAsync(fixture, experimentId, fixture.Clock.UtcNow, 0.00005m);
        DiagnosticExecutionRecord record = fixture.Store.Find(experimentId)!;
        fixture.Broker.LookupBehavior = _ => null;
        fixture.Broker.OpenOrders =
        [
            Order("diagnostic-open", record.ExitClientOrderId!, "new", 0, null),
            Order("external-open", "external-client-id", "new", 0, null)
        ];

        await fixture.Service.EmergencyFlattenAsync(experimentId, CancellationToken.None);

        Assert.Equal(1, fixture.Broker.CancelCount);
        Assert.Single(fixture.Broker.OpenOrders);
        Assert.Equal("external-client-id", fixture.Broker.OpenOrders[0].ClientOrderId);
        Assert.Equal(1, fixture.Broker.EmergencySubmitCount);
    }

    [Fact]
    public async Task Emergency_flatten_is_blocked_for_live_money_configuration()
    {
        using var fixture = new DiagnosticFixture(isPaperEnvironment: false);
        var record = new DiagnosticExecutionRecord(
            "live-emergency",
            "DiagnosticExecution",
            DiagnosticExecutionOptions.RequiredSymbol,
            "Holding",
            1m,
            TimeSpan.FromMinutes(2),
            fixture.Clock.UtcNow,
            "entry-id",
            "exit-id")
        {
            EmergencyClientOrderId = "emergency-id"
        };
        fixture.Store.Record(record);

        DiagnosticExecutionResult result = await fixture.Service.EmergencyFlattenAsync(
            record.ExperimentId, CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal("ALPACA_PAPER_REQUIRED", result.Reason);
        Assert.Equal(0, fixture.Broker.SubmitCount);
    }

    [Fact]
    public async Task Momentum_not_aligned_does_not_block_diagnostic_admission()
    {
        using var fixture = new DiagnosticFixture();
        decimal[] closes =
        [
            100m, 100.2m, 100.4m, 100.6m, 100.8m, 101m, 101.2m,
            101.4m, 101.6m, 102m, 101.8m, 101.6m, 101.4m
        ];
        CryptoResearchDecision research = new CryptoResearchGate().Evaluate(
            new DirectionalMarketEvidence(101.39m, 101.41m, closes));

        DiagnosticExecutionResult result = await fixture.PrepareAsync("momentum-independent");

        Assert.Equal("MOMENTUM_NOT_ALIGNED", research.Reason);
        Assert.True(result.Allowed);
    }

    [Fact]
    public async Task Features_not_ready_does_not_block_diagnostic_admission()
    {
        using var fixture = new DiagnosticFixture();
        fixture.Readiness.RecordResearchPlane(featuresReady: false, expertsReady: false);

        DiagnosticExecutionResult result = await fixture.PrepareAsync("features-independent");

        Assert.False(fixture.Readiness.Snapshot().FeaturesReady);
        Assert.False(fixture.Readiness.Snapshot().ExpertsReady);
        Assert.True(fixture.Readiness.Snapshot().InfrastructureExecutionReady);
        Assert.True(result.Allowed);
    }

    [Fact]
    public async Task Live_money_broker_configuration_blocks_diagnostic_admission()
    {
        using var fixture = new DiagnosticFixture(isPaperEnvironment: false);

        DiagnosticExecutionResult result = await fixture.Service.PrepareAsync(
            "live-blocked",
            DiagnosticExecutionOptions.RequiredSymbol,
            1m,
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal("ALPACA_PAPER_REQUIRED", result.Reason);
        Assert.Equal(0, fixture.Broker.SubmitCount);
    }

    [Fact]
    public async Task Unhealthy_crypto_account_blocks_diagnostic_admission()
    {
        using var fixture = new DiagnosticFixture();
        fixture.Broker.Account = fixture.Broker.Account with { CryptoTradingStatus = "INACTIVE" };

        DiagnosticExecutionResult result = await fixture.Service.PrepareAsync(
            "crypto-inactive",
            DiagnosticExecutionOptions.RequiredSymbol,
            1m,
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal("PAPER_ACCOUNT_UNAVAILABLE", result.Reason);
    }

    [Fact]
    public async Task Non_tradable_btc_asset_blocks_diagnostic_admission()
    {
        using var fixture = new DiagnosticFixture();
        fixture.Broker.Asset = fixture.Broker.Asset with { Tradable = false };

        DiagnosticExecutionResult result = await fixture.Service.PrepareAsync(
            "btc-not-tradable",
            DiagnosticExecutionOptions.RequiredSymbol,
            1m,
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal("BTC_USD_NOT_TRADABLE", result.Reason);
    }

    [Fact]
    public async Task Notional_above_diagnostic_risk_envelope_is_blocked()
    {
        using var fixture = new DiagnosticFixture();

        DiagnosticExecutionResult result = await fixture.Service.PrepareAsync(
            "risk-envelope",
            DiagnosticExecutionOptions.RequiredSymbol,
            5.01m,
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal("DIAGNOSTIC_RISK_ENVELOPE_EXCEEDED", result.Reason);
    }

    [Fact]
    public async Task Unavailable_persistence_blocks_diagnostic_admission()
    {
        using var fixture = new DiagnosticFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.StorePath)!);
        await File.WriteAllTextAsync(fixture.StorePath, "{malformed", CancellationToken.None);

        DiagnosticExecutionResult result = await fixture.Service.PrepareAsync(
            "persistence-blocked",
            DiagnosticExecutionOptions.RequiredSymbol,
            1m,
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal("PERSISTENCE_UNAVAILABLE", result.Reason);
    }

    [Fact]
    public async Task Unavailable_reconciliation_blocks_diagnostic_admission()
    {
        using var fixture = new DiagnosticFixture();
        fixture.Broker.ReconciliationAvailable = false;

        DiagnosticExecutionResult result = await fixture.Service.PrepareAsync(
            "reconciliation-blocked",
            DiagnosticExecutionOptions.RequiredSymbol,
            1m,
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal("RECONCILIATION_UNAVAILABLE", result.Reason);
    }

    [Theory]
    [InlineData("accepted", "EntryAccepted", true)]
    [InlineData("new", "EntryAccepted", true)]
    [InlineData("pending", "EntryAccepted", true)]
    [InlineData("canceled", "EntryCanceled", false)]
    [InlineData("rejected", "EntryRejected", false)]
    [InlineData("expired", "EntryExpired", false)]
    public async Task Broker_entry_statuses_map_to_durable_lifecycle(
        string brokerStatus,
        string expectedState,
        bool expectedAllowed)
    {
        using var fixture = new DiagnosticFixture();
        string experimentId = $"status-{brokerStatus}";
        await fixture.PrepareAsync(experimentId);
        await fixture.Service.AdvanceAsync(experimentId, 0.00001m, CancellationToken.None);
        fixture.Broker.LookupBehavior = clientOrderId =>
            Order("broker-status", clientOrderId, brokerStatus, 0, null);

        DiagnosticExecutionResult result = await fixture.Service.AdvanceAsync(
            experimentId, 0.00001m, CancellationToken.None);

        Assert.Equal(expectedAllowed, result.Allowed);
        Assert.Equal(expectedState, fixture.Store.Find(experimentId)!.State);
        Assert.Equal(1, fixture.Broker.SubmitCount);
    }

    [Fact]
    public async Task Unexplained_btc_order_blocks_before_entry_post()
    {
        using var fixture = new DiagnosticFixture();
        fixture.Broker.OpenOrders = [Order("external", "external-client", "new", 0, null)];

        DiagnosticExecutionResult result = await fixture.Service.PrepareAsync(
            "external-order",
            DiagnosticExecutionOptions.RequiredSymbol,
            1m,
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal("UNEXPLAINED_BROKER_EXPOSURE", result.Reason);
        Assert.Equal(0, fixture.Broker.SubmitCount);
    }

    [Fact]
    public async Task Unexplained_btc_position_blocks_before_entry_post()
    {
        using var fixture = new DiagnosticFixture();
        fixture.Broker.Positions =
        [
            new BrokerPositionSnapshot(
                DiagnosticExecutionOptions.RequiredSymbol,
                0,
                0.0001m,
                100_000m)
        ];

        DiagnosticExecutionResult result = await fixture.Service.PrepareAsync(
            "external-position",
            DiagnosticExecutionOptions.RequiredSymbol,
            1m,
            CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal("UNEXPLAINED_BROKER_EXPOSURE", result.Reason);
        Assert.Equal(0, fixture.Broker.SubmitCount);
    }

    private static async Task<DiagnosticExecutionRecord> EnterHoldingAsync(
        DiagnosticFixture fixture,
        string experimentId,
        DateTimeOffset filledAt,
        decimal quantity)
    {
        await fixture.PrepareAsync(experimentId);
        await fixture.Service.AdvanceAsync(experimentId, quantity, CancellationToken.None);
        DiagnosticExecutionRecord reserved = fixture.Store.Find(experimentId)!;
        BrokerOrderSnapshot filled = Order(
            "broker-entry-fill",
            reserved.EntryClientOrderId!,
            "filled",
            quantity,
            100_000m) with
        {
            UpdatedAt = filledAt,
            FilledAt = filledAt
        };
        fixture.Broker.LookupBehavior = clientOrderId => clientOrderId == reserved.EntryClientOrderId
            ? SetBrokerTruth(fixture.Broker, filled, quantity)
            : null;

        await fixture.Service.AdvanceAsync(experimentId, quantity, CancellationToken.None);

        DiagnosticExecutionRecord holding = fixture.Store.Find(experimentId)!;
        Assert.Equal("Holding", holding.State);
        return holding;
    }

    private static async Task<DiagnosticExecutionRecord> ProgressToExitDueAsync(
        DiagnosticFixture fixture,
        string experimentId,
        decimal quantity)
    {
        await EnterHoldingAsync(fixture, experimentId, fixture.Clock.UtcNow, quantity);
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        await fixture.Service.AdvanceAsync(experimentId, 0, CancellationToken.None);

        DiagnosticExecutionRecord exitDue = fixture.Store.Find(experimentId)!;
        Assert.Equal("ExitDue", exitDue.State);
        fixture.Broker.LookupBehavior = _ => null;
        fixture.Broker.OpenOrders = [];
        fixture.Broker.Positions =
        [
            new BrokerPositionSnapshot(
                DiagnosticExecutionOptions.RequiredSymbol,
                0,
                quantity,
                100_000m)
        ];
        return exitDue;
    }

    private static BrokerOrderSnapshot SetExitBrokerTruth(
        FakeBroker broker,
        BrokerOrderSnapshot order,
        decimal remainingPosition)
    {
        broker.OpenOrders = order.Status == "filled" ? [] : [order];
        broker.Positions = remainingPosition == 0
            ? []
            :
            [
                new BrokerPositionSnapshot(
                    DiagnosticExecutionOptions.RequiredSymbol,
                    0,
                    remainingPosition,
                    order.AverageFillPrice ?? 0)
            ];
        return order;
    }

    private static BrokerOrderSnapshot SetBrokerTruth(
        FakeBroker broker,
        BrokerOrderSnapshot order,
        decimal positionQuantity)
    {
        broker.OpenOrders = order.Status == "filled" ? [] : [order];
        broker.Positions =
        [
            new BrokerPositionSnapshot(
                DiagnosticExecutionOptions.RequiredSymbol,
                0,
                positionQuantity,
                order.AverageFillPrice ?? 0)
        ];
        return order;
    }

    private static BrokerSubmitResult Acknowledged(string brokerOrderId) =>
        new(BrokerSubmitState.Acknowledged, brokerOrderId, null, "request-1");

    private static BrokerOrderSnapshot Order(
        string brokerOrderId,
        string clientOrderId,
        string status,
        decimal filledQuantity,
        decimal? averageFillPrice) =>
        new(brokerOrderId, clientOrderId, status, filledQuantity, averageFillPrice)
        {
            Symbol = DiagnosticExecutionOptions.RequiredSymbol
        };

    private sealed class DiagnosticFixture : IDisposable
    {
        private readonly string _directory;

        public DiagnosticFixture(bool isPaperEnvironment = true)
        {
            _directory = Path.Combine(Path.GetTempPath(), $"quantdesk-diagnostic-{Guid.NewGuid():N}");
            StorePath = Path.Combine(_directory, "execution.json");
            Store = new DiagnosticExecutionStore(StorePath);
            Broker = new FakeBroker(isPaperEnvironment);
            Readiness = CreateInfrastructureReadyState();
            Clock = new VirtualRuntimeClock(DateTimeOffset.Parse("2026-08-30T00:00:00Z"));
            Service = CreateService(Store);
        }

        public string StorePath { get; }
        public DiagnosticExecutionStore Store { get; }
        public FakeBroker Broker { get; }
        public FullSystemReadinessState Readiness { get; }
        public VirtualRuntimeClock Clock { get; }
        public CryptoDiagnosticExecutionService Service { get; }

        // The emergency sub-lifecycle is built here rather than injected by a caller so that it and the
        // service it serves always share one store — a restart is simulated by swapping the store, and a
        // pair that disagreed about which store is authoritative would not model anything real.
        public CryptoDiagnosticExecutionService CreateService(DiagnosticExecutionStore store) =>
            new(
                Readiness,
                store,
                Broker,
                new DiagnosticExecutionOptions(5m),
                Symbols,
                Clock,
                new DiagnosticEmergencyFlatten(store, Broker, Symbols, Clock));

        private static DictionaryInstrumentSymbolResolver Symbols { get; } = new(
            new Dictionary<int, string> { [0] = DiagnosticExecutionOptions.RequiredSymbol });

        public async Task<DiagnosticExecutionResult> PrepareAsync(string experimentId)
        {
            DiagnosticExecutionResult result = await Service.PrepareAsync(
                experimentId,
                DiagnosticExecutionOptions.RequiredSymbol,
                1m,
                CancellationToken.None);
            Assert.True(result.Allowed, result.Reason);
            return result;
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }

        private static FullSystemReadinessState CreateInfrastructureReadyState()
        {
            var state = new FullSystemReadinessState();
            state.RecordBrokerPreflight(reconciled: true, portfolioKnown: true, paperEndpointVerified: true);
            state.RecordDeterministicRuntime(
                committeesReady: false,
                riskReady: true,
                reservationReady: true,
                executionReady: true,
                exitEngineReady: false);
            state.RecordStreams(marketDataHealthy: false, tradeUpdatesHealthy: false);
            return state;
        }
    }

    private sealed class FakeBroker(bool isPaperEnvironment) : IBrokerExecutionGateway
    {
        public bool IsPaperEnvironment { get; } = isPaperEnvironment;
        public List<ExecutionCommand> SubmittedCommands { get; } = [];
        public int SubmitCount => SubmittedCommands.Count;
        public int ExitSubmitCount => SubmittedCommands.Count(command => command.Side == OrderSide.Sell);
        public int EmergencySubmitCount => SubmittedCommands.Count(
            command => command.Priority == ExecutionPriority.EmergencyExit);
        public int CancelCount { get; private set; }
        public ExecutionCommand? LastSubmittedCommand => SubmittedCommands.LastOrDefault();
        public Func<ExecutionCommand, Task<BrokerSubmitResult>>? SubmitBehavior { get; set; }
        public Func<string, BrokerOrderSnapshot?>? LookupBehavior { get; set; }
        public IReadOnlyList<BrokerOrderSnapshot> OpenOrders { get; set; } = [];
        public IReadOnlyList<BrokerPositionSnapshot> Positions { get; set; } = [];
        public bool ReconciliationAvailable { get; set; } = true;

        public BrokerAccountSnapshot Account { get; set; } = new(
            "paper-account", "ACTIVE", 100_000m, 100_000m, false, false)
        {
            CryptoTradingStatus = "ACTIVE"
        };

        public BrokerAssetSnapshot Asset { get; set; } = new(
            DiagnosticExecutionOptions.RequiredSymbol, "active", "crypto", true);

        public Task<BrokerAccountSnapshot?> GetAccountAsync(CancellationToken cancellationToken) =>
            Task.FromResult<BrokerAccountSnapshot?>(Account);

        public Task<BrokerAssetSnapshot?> GetAssetAsync(string symbol, CancellationToken cancellationToken) =>
            Task.FromResult<BrokerAssetSnapshot?>(Asset);

        public Task<BrokerSubmitResult> SubmitAsync(
            ExecutionCommand command,
            CancellationToken cancellationToken)
        {
            SubmittedCommands.Add(command);
            return SubmitBehavior?.Invoke(command) ??
                Task.FromResult(Acknowledged($"broker-submit-{SubmitCount}"));
        }

        public Task<BrokerOrderSnapshot?> FindByClientOrderIdAsync(
            string clientOrderId,
            CancellationToken cancellationToken) =>
            Task.FromResult(LookupBehavior?.Invoke(clientOrderId));

        public Task<IReadOnlyList<BrokerOrderSnapshot>> ListOpenOrdersForSymbolAsync(
            string symbol,
            CancellationToken cancellationToken) => ReconciliationAvailable
            ? Task.FromResult(OpenOrders)
            : Task.FromException<IReadOnlyList<BrokerOrderSnapshot>>(
                new HttpRequestException("simulated reconciliation outage"));

        public Task<IReadOnlyList<BrokerPositionSnapshot>> ListPositionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Positions);

        public Task<BrokerSubmitResult> CancelAsync(
            string brokerOrderId,
            CancellationToken cancellationToken)
        {
            CancelCount++;
            OpenOrders = OpenOrders.Where(order => order.BrokerOrderId != brokerOrderId).ToArray();
            return Task.FromResult(Acknowledged(brokerOrderId));
        }
    }
}
