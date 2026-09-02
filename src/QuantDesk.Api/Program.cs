using System.Globalization;
using QuantDesk.Alpaca.Capabilities;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Alpaca.Mapping;
using QuantDesk.Alpaca.MarketData;
using QuantDesk.Alpaca.Trading;
using QuantDesk.Api.PaperTrading;
using QuantDesk.Api.Security;
using QuantDesk.Domain.Contracts;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Market;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Risk;
using QuantDesk.Domain.Runtime;
using QuantDesk.Domain.Trading;
using QuantDesk.Runtime.Actionability;
using QuantDesk.Runtime.Costs;
using QuantDesk.Runtime.Execution;
using QuantDesk.Runtime.Experts;
using QuantDesk.Runtime.Ingestion;
using QuantDesk.Runtime.Modes;
using QuantDesk.Runtime.Options;
using QuantDesk.Runtime.Persistence;
using QuantDesk.Runtime.Positions;
using QuantDesk.Runtime.Risk;
using QuantDesk.Runtime.State;
using QuantDesk.Runtime.Strategies;
using QuantDesk.Runtime.Time;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton<IRuntimeClock, LiveRuntimeClock>();
builder.Services.AddSingleton<RuntimeModeState>();
builder.Services.AddSingleton<FullSystemReadinessState>();
builder.Services.AddSingleton<ExecutionAdmissionPolicy>();
builder.Services.AddSingleton<ResearchArtifactState>();
builder.Services.AddSingleton<OperatorKeyAuthorizer>();
builder.Services.AddSingleton(AlpacaOptions.FromEnvironment());
builder.Services.AddSingleton(PaperTradingOptions.FromEnvironment());
builder.Services.AddSingleton(services => DiagnosticExecutionOptions.FromEnvironment(
    services.GetRequiredService<PaperTradingOptions>()));
builder.Services.AddSingleton(services =>
{
    string configured = Environment.GetEnvironmentVariable("QUANTDESK_DIAGNOSTIC_STORE_PATH")
        ?? Path.Combine(AppContext.BaseDirectory, "runtime-data", "diagnostic-executions.json");
    return new DiagnosticExecutionStore(Path.GetFullPath(configured));
});
// Every lane that can hold exposure registers a claim source, so the entry gate can tell exposure this
// system created from exposure nobody can account for. A lane added without one is reported as foreign,
// which halts entry rather than trading over it.
builder.Services.AddSingleton<IExposureClaimSource, DiagnosticExposureClaimSource>();
builder.Services.AddSingleton<IExposureClaimSource, SpotExposureClaimSource>();
builder.Services.AddSingleton<IExposureClaimSource, MultiLegExposureClaimSource>();
builder.Services.AddSingleton<BrokerExposureAttributor>();
builder.Services.AddSingleton<DiagnosticEmergencyFlatten>();
builder.Services.AddSingleton<CryptoDiagnosticExecutionService>();
builder.Services.AddSingleton<DiagnosticExecutionRecoveryService>();
builder.Services.AddHostedService(services =>
    services.GetRequiredService<DiagnosticExecutionRecoveryService>());
builder.Services.AddSingleton(services => AutonomousPaperTradingOptions.FromEnvironment(
    services.GetRequiredService<PaperTradingOptions>()));
builder.Services.AddSingleton(services =>
{
    string configured = Environment.GetEnvironmentVariable("QUANTDESK_MLEG_STORE_PATH")
        ?? Path.Combine(AppContext.BaseDirectory, "runtime-data", "mleg-executions.json");
    return new MultiLegExecutionStore(Path.GetFullPath(configured));
});
builder.Services.AddSingleton(services =>
{
    string configured = Environment.GetEnvironmentVariable("QUANTDESK_AUTONOMOUS_STORE_PATH")
        ?? Path.Combine(AppContext.BaseDirectory, "runtime-data", "autonomous-executions.json");
    return new AutonomousExecutionStore(Path.GetFullPath(configured));
});
builder.Services.AddSingleton<IInstrumentSymbolResolver>(services =>
    new DictionaryInstrumentSymbolResolver(services.GetRequiredService<PaperTradingOptions>().Symbols));
builder.Services.AddSingleton<PaperOrderApplicationService>();
// The gate is asset-class agnostic by design -- it takes a cost profile -- but it was registered
// without one, so it fell back to the spot-crypto taker default whatever the lane traded. Pointed
// at SPY that charges an ~80 bps round-trip hurdle against an instrument whose real cost is nearer
// 9, and SPY does not move 80 bps in an hour: the lane would have abstained with
// EXPECTED_EDGE_BELOW_COSTS at every cycle, forever, while looking like it was working.
builder.Services.AddSingleton(services =>
{
    AutonomousPaperTradingOptions configured = services.GetRequiredService<AutonomousPaperTradingOptions>();
    return services.GetRequiredService<OpportunityRouter>()
        .TryRoute(configured.Symbol, out OpportunityRoute? route, out _) && route is not null
        ? new CryptoResearchGate(route.Costs)
        : new CryptoResearchGate();
});
builder.Services.AddSingleton(services =>
    new MarketStateStore(services.GetRequiredService<PaperTradingOptions>().Symbols.Count));
builder.Services.AddSingleton(new BoundedEventChannel<NormalizedMarketEvent>(8_192));
builder.Services.AddSingleton(new MicrostructureEvidenceBuffer(16_384));
builder.Services.AddSingleton<MarketStateOwner>();
builder.Services.AddSingleton<IAlpacaMarketDataParser>(services =>
    new AlpacaMarketDataParser(services.GetRequiredService<PaperTradingOptions>().Symbols
        .ToDictionary(item => item.Value, item => item.Key, StringComparer.OrdinalIgnoreCase)));
builder.Services.AddSingleton(services =>
{
    AlpacaOptions alpaca = services.GetRequiredService<AlpacaOptions>();
    return new AlpacaMarketDataStream(
        new Uri("wss://stream.data.alpaca.markets/v1beta3/crypto/us"),
        alpaca.KeyId,
        alpaca.SecretKey,
        services.GetRequiredService<IAlpacaMarketDataParser>(),
        (message, exception) =>
        {
            ILogger<AlpacaMarketDataStream> logger = services.GetRequiredService<ILogger<AlpacaMarketDataStream>>();
            if (exception is null) logger.LogWarning("{Message}", message);
            else logger.LogWarning(exception, "{Message}", message);
        });
});
builder.Services.AddSingleton(services =>
{
    AlpacaOptions alpaca = services.GetRequiredService<AlpacaOptions>();
    return new AlpacaTradeUpdateStream(
        new Uri("wss://paper-api.alpaca.markets/stream"),
        alpaca.KeyId,
        alpaca.SecretKey);
});
builder.Services.AddSingleton(new ExpertCommittee(0.60, 1));
builder.Services.AddSingleton(services =>
{
    AutonomousPaperTradingOptions configured = services.GetRequiredService<AutonomousPaperTradingOptions>();

    // The lane's own instrument decides which permission is required and which beta the position is
    // booked against. Assuming crypto meant an equity lane asked the venue for a permission it does
    // not need and reported its exposure to the risk governor under the wrong factor entirely.
    TradedAssetClass assetClass =
        services.GetRequiredService<OpportunityRouter>()
            .TryRoute(configured.Symbol, out OpportunityRoute? route, out _) && route is not null
            ? route.AssetClass
            : TradedAssetClass.SpotCrypto;

    return new CryptoDirectionalStrategyCompiler(
        new Usd(configured.OrderNotional), 0.05, TimeSpan.FromMinutes(5), configured.HoldDuration,
        assetClass);
});
builder.Services.AddSingleton(CryptoFeeSchedule.AlpacaTier1(DateTimeOffset.UtcNow));
builder.Services.AddSingleton<IRealisedCostSource>(services =>
    new DiagnosticStoreRealisedCostSource(
        services.GetRequiredService<DiagnosticExecutionStore>(),
        services.GetRequiredService<SpotExecutionStore>()));

// The cost the decision actually gets charged.
//
// The modelled figure alone was Alpaca's published 50 bps schedule rate plus the live spread. The
// account lost 68 bps per round trip, because the venue also takes a USD cash charge that appears
// in no fill. Every candidate whose edge sat between the two looked profitable and was not. The
// floor charges whichever of the two is more pessimistic, so a widening spread still raises the
// charge and a fee the model has never heard of still cannot be traded through.
builder.Services.AddSingleton<ICostModel>(services =>
{
    CryptoFeeSchedule fees = services.GetRequiredService<CryptoFeeSchedule>();
    var modelled = new CryptoCostModel(
        new BasisPoints((double)(fees.TakerBps * 2m)), new BasisPoints(10));
    return new MeasuredCostFloor(modelled, services.GetRequiredService<IRealisedCostSource>());
});
builder.Services.AddSingleton(new ActionabilityGate(0.01, new Usd(0.01m)));
builder.Services.AddSingleton(services => new RiskGovernor(
    RiskLimitOptions.FromEnvironment(
        services.GetRequiredService<AutonomousPaperTradingOptions>().OrderNotional)));
builder.Services.AddSingleton<ExitEngine>();
builder.Services.AddSingleton<AutonomousDecisionPipeline>();
builder.Services.AddSingleton<AutonomousTradingState>();
// Registered as a singleton and then hosted from it, so /api/system/resume can ask for one
// reconciliation pass on demand rather than waiting out the 30-second timer.
builder.Services.AddSingleton<PaperRuntimePreflightService>();
builder.Services.AddHostedService(services =>
    services.GetRequiredService<PaperRuntimePreflightService>());
builder.Services.AddHostedService<AutonomousPaperTradingService>();
builder.Services.AddHostedService<MarketDataRuntimeService>();
builder.Services.AddHostedService<MicrostructureEvidenceCaptureService>();
builder.Services.AddHostedService<CryptoQuoteCaptureService>();
builder.Services.AddHostedService<TradeUpdateRuntimeService>();
builder.Services.AddHostedService<HistoricalCryptoDatasetService>();
builder.Services.AddHostedService<HistoricalEquityDatasetService>();
builder.Services.AddHttpClient<ResearchReadinessMonitorService>(client =>
{
    string configured = Environment.GetEnvironmentVariable("QUANTDESK_RESEARCH_BASE_URL")
        ?? "http://localhost:8000";
    client.BaseAddress = new Uri(configured.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddHostedService<ResearchReadinessMonitorService>(
    services => services.GetRequiredService<ResearchReadinessMonitorService>());
builder.Services.AddHostedService<ResearchArtifactMonitorService>();
builder.Services.AddHttpClient<IAlpacaCapabilityProbe, AlpacaCapabilityProbe>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient<AlpacaTradingGateway>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddTransient<IBrokerExecutionGateway>(services =>
    services.GetRequiredService<AlpacaTradingGateway>());
builder.Services.AddTransient<IMultiLegBrokerExecutionGateway>(services =>
    services.GetRequiredService<AlpacaTradingGateway>());
builder.Services.AddSingleton(services => new MultiLegExecutionLifecycle(
    services.GetRequiredService<IMultiLegBrokerExecutionGateway>(),
    services.GetRequiredService<IBrokerExecutionGateway>(),
    services.GetRequiredService<MultiLegExecutionStore>(),
    services.GetRequiredService<IRuntimeClock>(),
    services.GetRequiredService<AutonomousPaperTradingOptions>().FillTimeout,
    services.GetRequiredService<IHoldInterrupt>()));
builder.Services.AddSingleton(services =>
{
    string configured = Environment.GetEnvironmentVariable("QUANTDESK_SPOT_STORE_PATH")
        ?? Path.Combine(AppContext.BaseDirectory, "runtime-data", "spot-executions.json");
    return new SpotExecutionStore(Path.GetFullPath(configured));
});
builder.Services.AddSingleton<IHeldPositionMarker>(services => new MarketStateHeldPositionMarker(
    services.GetRequiredService<MarketStateStore>(),
    services.GetRequiredService<IInstrumentSymbolResolver>()));

// The reasons a hold may end early. Until this existed the only one was the clock: a position whose
// research had been retracted ran to its timer, and a position past its defined maximum loss ran to
// its timer, because that maximum sized the capital reservation but was never compared to anything.
// Retraction is listed first so it names the exit when both fire at once.
builder.Services.AddSingleton<IHoldInterrupt>(services => new CompositeHoldInterrupt(
    new ArtifactRetractionHoldInterrupt(services.GetRequiredService<ResearchArtifactState>()),
    // Options only. A defined-risk vertical days from expiry is not the position that was opened
    // with weeks to run: gamma rises, spreads widen as makers step back, and assignment on the
    // short leg becomes real. MinimumDteToHold existed on the management plan to say this and was
    // passed as null by every compiler, so the rule was stated in the domain and absent from the
    // system. Spot carries no expiry and the rule correctly ignores it.
    new ExpiryHoldInterrupt(services.GetRequiredService<IRuntimeClock>(), minimumDaysToExpiry: 2),
    new AdverseLossHoldInterrupt(services.GetRequiredService<IHeldPositionMarker>())));

builder.Services.AddSingleton(services => new SpotExecutionLifecycle(
    services.GetRequiredService<IBrokerExecutionGateway>(),
    services.GetRequiredService<SpotExecutionStore>(),
    services.GetRequiredService<IRuntimeClock>(),
    services.GetRequiredService<AutonomousPaperTradingOptions>().FillTimeout,
    services.GetRequiredService<IHoldInterrupt>(),
    // Supplies the closing decision price when an execution reconciles flat, so the lane can
    // measure what its own round trip cost instead of depending on another lane to do it.
    services.GetRequiredService<IHeldPositionMarker>()));
builder.Services.AddHostedService<RealisedCostPublisherService>();
builder.Services.AddSingleton<SpotExecutionRecoveryService>();
builder.Services.AddHostedService(services =>
    services.GetRequiredService<SpotExecutionRecoveryService>());
builder.Services.AddSingleton<MultiLegExecutionRecoveryService>();
builder.Services.AddHostedService(services =>
    services.GetRequiredService<MultiLegExecutionRecoveryService>());
builder.Services.AddHttpClient<QuantDesk.Alpaca.MarketData.AlpacaMarketClock>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient<QuantDesk.Alpaca.MarketData.AlpacaLatestCryptoQuoteClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient<AlpacaHistoricalStockBarClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient<AlpacaHistoricalOptionBarClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient<AlpacaOptionContractClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddSingleton<OptionResearchDatasetExporter>();
builder.Services.AddHttpClient<AlpacaLatestOptionQuoteClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient<AlpacaOptionRiskSnapshotClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient<AlpacaLatestEquityQuoteClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton<OpportunityRouter>();
builder.Services.AddSingleton<MarketEvidenceProvider>();
builder.Services.AddSingleton<IMarketEvidenceProvider>(services =>
    services.GetRequiredService<MarketEvidenceProvider>());
// A defined-risk vertical's whole safety property is that the debit paid is the maximum loss, so
// the risk budget per spread is the hard cap on what one options opportunity can cost. It is
// derived from the same notional envelope the spot lane uses rather than invented here.
builder.Services.AddSingleton(services =>
{
    AutonomousPaperTradingOptions trading = services.GetRequiredService<AutonomousPaperTradingOptions>();
    return new DefinedRiskVerticalCompiler(
        riskBudgetPerSpread: new Usd(trading.OrderNotional),
        maximumRelativeSpread: 0.15,
        minimumRewardToRisk: 0.5,
        minimumDaysToExpiry: 7,
        maximumDaysToExpiry: 60);
});
builder.Services.AddSingleton<OptionVerticalOpportunityService>();
builder.Services.AddSingleton<OptionExecutionCoordinator>();
builder.Services.AddSingleton<DefinedRiskVerticalLifecycleService>();

WebApplication app = builder.Build();
app.Services.GetRequiredService<FullSystemReadinessState>().RecordDeterministicRuntime(
    committeesReady: true,
    riskReady: true,
    reservationReady: true,
    executionReady: true,
    exitEngineReady: true);
app.UseExceptionHandler();
app.MapHealthChecks("/health");
app.MapGet("/ready", (RuntimeModeState runtimeMode) =>
    runtimeMode.Snapshot().Mode == SystemMode.Ready
        ? Results.Ok(new { ready = true })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
app.MapGet("/api/system/status", (RuntimeModeState runtimeMode, IRuntimeClock clock) =>
{
    var snapshot = runtimeMode.Snapshot();
    return Results.Ok(new
    {
        mode = snapshot.Mode.ToString(),
        reason = snapshot.Reason,
        utcNow = clock.UtcNow
    });
});
app.MapGet("/api/system/readiness", (FullSystemReadinessState readiness) =>
{
    FullSystemReadinessSnapshot snapshot = readiness.Snapshot();
    return Results.Json(snapshot, statusCode: snapshot.Ready
        ? StatusCodes.Status200OK
        : StatusCodes.Status503ServiceUnavailable);
});
app.MapGet("/api/system/capabilities", async (
    IAlpacaCapabilityProbe probe,
    FullSystemReadinessState readiness,
    CancellationToken cancellationToken) =>
{
    var capabilities = await probe.ProbeAsync(cancellationToken);
    return Results.Ok(capabilities with
    {
        TradeUpdateStream = readiness.Snapshot().TradeUpdatesHealthy
    });
});
app.MapGet("/api/autonomous/status", (AutonomousTradingState autonomous) =>
    Results.Ok(autonomous.Snapshot()));
app.MapGet("/api/research/status", (ResearchArtifactState artifacts) =>
    Results.Ok(artifacts.Snapshot()));
app.MapGet("/api/research/microstructure-status", (MicrostructureEvidenceBuffer evidence) =>
    Results.Ok(evidence.Snapshot()));
app.MapGet("/api/diagnostics/recovery", (
    HttpRequest request,
    DiagnosticExecutionRecoveryService recovery,
    OperatorKeyAuthorizer authorizer) =>
{
    if (!authorizer.IsAuthorized(request.Headers["X-QuantDesk-Operator-Key"].FirstOrDefault()))
        return Results.Unauthorized();
    return Results.Ok(new
    {
        active = recovery.StartedAt is not null && recovery.LastError is null,
        recovery.StartedAt,
        recovery.LastCycleAt,
        recovery.LastError
    });
});
app.MapGet("/api/options/recovery", (
    HttpRequest request,
    MultiLegExecutionRecoveryService recovery,
    MultiLegExecutionStore store,
    OperatorKeyAuthorizer authorizer) =>
{
    if (!authorizer.IsAuthorized(request.Headers["X-QuantDesk-Operator-Key"].FirstOrDefault()))
        return Results.Unauthorized();
    return Results.Ok(new
    {
        active = recovery.StartedAt is not null && recovery.LastError is null,
        recovery.StartedAt,
        recovery.LastCycleAt,
        recovery.LastError,
        nonterminalCount = store.ListNonterminal().Count
    });
});
app.MapPost("/api/options/{executionId}/emergency-flatten", async (
    HttpRequest request,
    string executionId,
    MultiLegExecutionLifecycle lifecycle,
    MultiLegExecutionStore store,
    OperatorKeyAuthorizer authorizer,
    CancellationToken cancellationToken) =>
{
    if (!authorizer.IsAuthorized(request.Headers["X-QuantDesk-Operator-Key"].FirstOrDefault()))
        return Results.Unauthorized();
    if (store.Find(executionId) is null)
        return Results.NotFound();
    MultiLegExecutionLifecycle.EmergencyFlattenResult result = await lifecycle.EmergencyFlattenAsync(
        executionId, cancellationToken);
    return Results.Json(result, statusCode: result.Complete
        ? StatusCodes.Status200OK
        : result.Pending ? StatusCodes.Status202Accepted : StatusCodes.Status409Conflict);
});
app.MapGet("/api/diagnostics/{experimentId}", (
    HttpRequest request,
    string experimentId,
    DiagnosticExecutionStore store,
    OperatorKeyAuthorizer authorizer) =>
{
    if (!authorizer.IsAuthorized(request.Headers["X-QuantDesk-Operator-Key"].FirstOrDefault()))
        return Results.Unauthorized();
    DiagnosticExecutionRecord? record = store.Find(experimentId);
    return record is null ? Results.NotFound() : Results.Ok(record);
});
app.MapGet("/api/costs/realised", (
    HttpRequest request,
    IRealisedCostSource realisedCosts,
    OperatorKeyAuthorizer authorizer) =>
{
    if (!authorizer.IsAuthorized(request.Headers["X-QuantDesk-Operator-Key"].FirstOrDefault()))
        return Results.Unauthorized();

    // The same source the decision path charges against, so what an operator reads here is what a
    // trade is actually priced with. Derived on read: a cached copy would keep answering after new
    // evidence had arrived.
    RealisedCostContract? contract = realisedCosts.Current();

    // 404 rather than an empty dataset or a zero. Too few completed round trips is not a cost of
    // zero; it is the absence of a measurement, and the caller has to be able to tell the
    // difference before deciding whether to trade on it.
    return contract is null
        ? Results.NotFound(new { reason = "InsufficientCompletedRoundTrips" })
        : Results.Ok(contract);
});
app.MapPost("/api/diagnostics/{experimentId}/start", async (
    HttpRequest request,
    string experimentId,
    CryptoDiagnosticExecutionService diagnostics,
    DiagnosticExecutionOptions diagnosticOptions,
    DiagnosticExecutionStore store,
    IBrokerExecutionGateway broker,
    OperatorKeyAuthorizer authorizer,
    CancellationToken cancellationToken) =>
{
    if (!authorizer.IsAuthorized(request.Headers["X-QuantDesk-Operator-Key"].FirstOrDefault()))
        return Results.Unauthorized();

    DiagnosticExecutionRecord? existing = store.Find(experimentId);
    DiagnosticExecutionResult result = existing is null
        ? await diagnostics.PrepareAsync(
            experimentId,
            DiagnosticExecutionOptions.RequiredSymbol,
            diagnosticOptions.MaximumNotional,
            cancellationToken)
        : DiagnosticExecutionResult.Ready(
            existing.ExperimentId,
            existing.EntryClientOrderId!,
            existing.ExitClientOrderId!);
    if (!result.Allowed) return Results.Json(result, statusCode: StatusCodes.Status409Conflict);

    DiagnosticExecutionRecord reserved = store.Find(experimentId)!;
    if (reserved.State == "EntryRejected" &&
        reserved.EntryBrokerOrderId is null &&
        reserved.FailureReason?.StartsWith("BROKER_", StringComparison.Ordinal) == true &&
        await broker.FindByClientOrderIdAsync(reserved.EntryClientOrderId!, cancellationToken) is null)
    {
        store.Update(experimentId, current => current with
        {
            State = "EntryReserved",
            RequestedNotional = diagnosticOptions.MaximumNotional,
            EntrySubmissionAttemptedAt = null,
            Failure = DiagnosticExecutionFailure.None,
            FailureReason = null
        });
        reserved = store.Find(experimentId)!;
    }
    if (reserved.State == "EntryReserved" && reserved.EntrySubmissionAttemptedAt is null)
        result = await diagnostics.AdvanceAsync(
            experimentId, DiagnosticExecutionOptions.MinimumCryptoQuantity, cancellationToken);
    else if (reserved.State == "ReconciliationFailed")
    {
        store.Update(experimentId, current => current with
        {
            State = "Reconciling",
            Failure = DiagnosticExecutionFailure.None,
            FailureReason = null
        });
        result = await diagnostics.AdvanceAsync(experimentId, 0, cancellationToken);
    }
    else if (reserved.State == "Complete" && reserved.GrossPaperPnl is null)
        result = await diagnostics.AdvanceAsync(experimentId, 0, cancellationToken);
    return Results.Json(new { result, record = store.Find(experimentId) }, statusCode:
        result.Allowed ? StatusCodes.Status202Accepted : StatusCodes.Status409Conflict);
});
app.MapPost("/api/system/halt", (
    HttpRequest request,
    RuntimeModeState runtimeMode,
    OperatorKeyAuthorizer authorizer) =>
{
    if (!authorizer.IsAuthorized(request.Headers["X-QuantDesk-Operator-Key"].FirstOrDefault()))
        return Results.Unauthorized();
    runtimeMode.Transition(SystemMode.EntryHalted, "operator_halt");
    return Results.Ok(new { mode = SystemMode.EntryHalted.ToString() });
});
// Clearing an operator halt.
//
// Halt and risk-reduction are deliberately sticky: the preflight preserves them so a routine
// reconciliation cycle cannot quietly undo a human decision. Without a way to release them, though,
// the only route back to Ready was restarting the process — an operator could stop the system and
// then not start it. This hands the decision back to the preflight rather than forcing Ready
// directly, so the system resumes only if it independently reconciles.
app.MapPost("/api/system/resume", async (
    HttpRequest request,
    RuntimeModeState runtimeMode,
    PaperRuntimePreflightService preflight,
    FullSystemReadinessState readiness,
    OperatorKeyAuthorizer authorizer,
    CancellationToken cancellationToken) =>
{
    if (!authorizer.IsAuthorized(request.Headers["X-QuantDesk-Operator-Key"].FirstOrDefault()))
        return Results.Unauthorized();

    (SystemMode mode, string? reason) = runtimeMode.Snapshot();
    if (mode is not (SystemMode.EntryHalted or SystemMode.RiskReductionOnly))
        return Results.Json(new { mode = mode.ToString(), resumed = false, reason = "NOT_HALTED" });

    runtimeMode.Transition(SystemMode.Syncing, "operator_resume");
    await preflight.CheckOnceAsync(cancellationToken);
    SystemMode resulting = runtimeMode.Snapshot().Mode;
    FullSystemReadinessSnapshot snapshot = readiness.Snapshot();

    // A bare "resumed: false" tells an operator nothing about what to fix. Name the gates that are
    // still down, and say separately whether execution can proceed at all — full readiness includes
    // the research plane, which is not a prerequisite for the diagnostic or manual order paths.
    string[] blocking =
    [
        .. new (string Name, bool Ready)[]
        {
            ("marketDataHealthy", snapshot.MarketDataHealthy),
            ("tradeUpdatesHealthy", snapshot.TradeUpdatesHealthy),
            ("brokerReconciled", snapshot.BrokerReconciled),
            ("portfolioKnown", snapshot.PortfolioKnown),
            ("featuresReady", snapshot.FeaturesReady),
            ("expertsReady", snapshot.ExpertsReady),
            ("committeesReady", snapshot.CommitteesReady),
            ("riskReady", snapshot.RiskReady),
            ("reservationReady", snapshot.ReservationReady),
            ("executionReady", snapshot.ExecutionReady),
            ("exitEngineReady", snapshot.ExitEngineReady),
            ("paperEndpointVerified", snapshot.PaperEndpointVerified)
        }.Where(gate => !gate.Ready).Select(gate => gate.Name)
    ];

    return Results.Json(new
    {
        mode = resulting.ToString(),
        resumed = resulting == SystemMode.Ready,
        previous = mode.ToString(),
        previousReason = reason,
        infrastructureExecutionReady = snapshot.InfrastructureExecutionReady,
        exitExecutionReady = snapshot.ExitExecutionReady,
        blockingFullReadiness = blocking
    });
});
app.MapPost("/api/system/risk-reduction", (
    HttpRequest request,
    RuntimeModeState runtimeMode,
    OperatorKeyAuthorizer authorizer) =>
{
    if (!authorizer.IsAuthorized(request.Headers["X-QuantDesk-Operator-Key"].FirstOrDefault()))
        return Results.Unauthorized();
    runtimeMode.Transition(SystemMode.RiskReductionOnly, "operator_risk_reduction");
    return Results.Ok(new { mode = SystemMode.RiskReductionOnly.ToString() });
});
app.MapGet("/api/paper/orders", async (
    HttpRequest request,
    PaperOrderApplicationService orders,
    OperatorKeyAuthorizer authorizer,
    CancellationToken cancellationToken) =>
{
    if (!authorizer.IsAuthorized(request.Headers["X-QuantDesk-Operator-Key"].FirstOrDefault()))
        return Results.Unauthorized();
    return Results.Ok(await orders.ListOpenAsync(cancellationToken));
});
app.MapPost("/api/paper/orders", async (
    HttpRequest request,
    PaperOrderRequest order,
    PaperOrderApplicationService orders,
    OperatorKeyAuthorizer authorizer,
    CancellationToken cancellationToken) =>
{
    if (!authorizer.IsAuthorized(request.Headers["X-QuantDesk-Operator-Key"].FirstOrDefault()))
        return Results.Unauthorized();
    PaperOrderSubmission result = await orders.SubmitAsync(order, cancellationToken);
    return Results.Json(result, statusCode: result.Accepted
        ? StatusCodes.Status202Accepted
        : StatusCodes.Status422UnprocessableEntity);
});
app.MapDelete("/api/paper/orders/{brokerOrderId}", async (
    HttpRequest request,
    string brokerOrderId,
    PaperOrderApplicationService orders,
    OperatorKeyAuthorizer authorizer,
    CancellationToken cancellationToken) =>
{
    if (!authorizer.IsAuthorized(request.Headers["X-QuantDesk-Operator-Key"].FirstOrDefault()))
        return Results.Unauthorized();
    BrokerSubmitResult result = await orders.CancelAsync(brokerOrderId, cancellationToken);
    return Results.Json(result, statusCode: result.State == BrokerSubmitState.Acknowledged
        ? StatusCodes.Status200OK
        : StatusCodes.Status502BadGateway);
});

app.Run();

public partial class Program;
