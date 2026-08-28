using QuantDesk.Alpaca.Capabilities;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Alpaca.Mapping;
using QuantDesk.Alpaca.Trading;
using QuantDesk.Api.PaperTrading;
using QuantDesk.Runtime.Modes;
using QuantDesk.Runtime.Time;
using QuantDesk.Api.Security;
using QuantDesk.Domain.Execution;
using QuantDesk.Domain.Runtime;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton<IRuntimeClock, LiveRuntimeClock>();
builder.Services.AddSingleton<RuntimeModeState>();
builder.Services.AddSingleton<OperatorKeyAuthorizer>();
builder.Services.AddSingleton(AlpacaOptions.FromEnvironment());
builder.Services.AddSingleton(PaperTradingOptions.FromEnvironment());
builder.Services.AddSingleton<IInstrumentSymbolResolver>(services =>
    new DictionaryInstrumentSymbolResolver(services.GetRequiredService<PaperTradingOptions>().Symbols));
builder.Services.AddSingleton<PaperOrderApplicationService>();
builder.Services.AddHostedService<PaperRuntimePreflightService>();
builder.Services.AddHttpClient<IAlpacaCapabilityProbe, AlpacaCapabilityProbe>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient<IBrokerExecutionGateway, AlpacaTradingGateway>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});

WebApplication app = builder.Build();
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
app.MapGet("/api/system/capabilities", async (
    IAlpacaCapabilityProbe probe,
    CancellationToken cancellationToken) =>
    Results.Ok(await probe.ProbeAsync(cancellationToken)));
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
