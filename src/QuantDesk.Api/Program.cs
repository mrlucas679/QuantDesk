using QuantDesk.Alpaca.Capabilities;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Alpaca.Mapping;
using QuantDesk.Alpaca.MarketData;
using QuantDesk.Alpaca.Trading;
using QuantDesk.Api.PaperTrading;
using QuantDesk.Runtime.Modes;
using QuantDesk.Runtime.Time;
using QuantDesk.Api.Security;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Risk;
using QuantDesk.Domain.Runtime;
using QuantDesk.Runtime.Actionability;
using QuantDesk.Runtime.Costs;
using QuantDesk.Runtime.Experts;
using QuantDesk.Runtime.Positions;
using QuantDesk.Runtime.Risk;
using QuantDesk.Runtime.State;
using QuantDesk.Runtime.Strategies;
using QuantDesk.Domain.Market;
using QuantDesk.Runtime.Ingestion;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton<IRuntimeClock, LiveRuntimeClock>();
builder.Services.AddSingleton<RuntimeModeState>();
builder.Services.AddSingleton<FullSystemReadinessState>();
builder.Services.AddSingleton<ResearchArtifactState>();
builder.Services.AddSingleton<OperatorKeyAuthorizer>();
builder.Services.AddSingleton(AlpacaOptions.FromEnvironment());
builder.Services.AddSingleton(PaperTradingOptions.FromEnvironment());
builder.Services.AddSingleton(services => AutonomousPaperTradingOptions.FromEnvironment(
    services.GetRequiredService<PaperTradingOptions>()));
builder.Services.AddSingleton<IInstrumentSymbolResolver>(services =>
    new DictionaryInstrumentSymbolResolver(services.GetRequiredService<PaperTradingOptions>().Symbols));
builder.Services.AddSingleton<PaperOrderApplicationService>();
builder.Services.AddSingleton<CryptoResearchGate>();
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
    return new CryptoDirectionalStrategyCompiler(
        new Usd(configured.OrderNotional), 0.05, TimeSpan.FromMinutes(5), configured.HoldDuration);
});
builder.Services.AddSingleton(new CryptoCostModel(new BasisPoints(50), new BasisPoints(10)));
builder.Services.AddSingleton(new ActionabilityGate(0.01, new Usd(0.01m)));
builder.Services.AddSingleton(new RiskGovernor(new RiskLimits(
    new Usd(5), new Usd(25), new Usd(100), new Usd(250), 1,
    100_000, 100_000, 100_000, 0.01, 1)));
builder.Services.AddSingleton<ExitEngine>();
builder.Services.AddSingleton<AutonomousDecisionPipeline>();
builder.Services.AddSingleton<AutonomousTradingState>();
builder.Services.AddHostedService<PaperRuntimePreflightService>();
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
builder.Services.AddHttpClient<IBrokerExecutionGateway, AlpacaTradingGateway>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient<QuantDesk.Alpaca.MarketData.AlpacaLatestCryptoQuoteClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient<AlpacaHistoricalStockBarClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});

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
    CancellationToken cancellationToken) =>
    Results.Ok(await probe.ProbeAsync(cancellationToken)));
app.MapGet("/api/autonomous/status", (AutonomousTradingState autonomous) =>
    Results.Ok(autonomous.Snapshot()));
app.MapGet("/api/research/status", (ResearchArtifactState artifacts) =>
    Results.Ok(artifacts.Snapshot()));
app.MapGet("/api/research/microstructure-status", (MicrostructureEvidenceBuffer evidence) =>
    Results.Ok(evidence.Snapshot()));
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
