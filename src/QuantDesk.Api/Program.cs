using QuantDesk.Alpaca.Capabilities;
using QuantDesk.Alpaca.Configuration;
using QuantDesk.Runtime.Modes;
using QuantDesk.Runtime.Time;
using QuantDesk.Api.Security;
using QuantDesk.Domain.Runtime;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton<IRuntimeClock, LiveRuntimeClock>();
builder.Services.AddSingleton<RuntimeModeState>();
builder.Services.AddSingleton<OperatorKeyAuthorizer>();
builder.Services.AddSingleton(AlpacaOptions.FromEnvironment());
builder.Services.AddHttpClient<IAlpacaCapabilityProbe, AlpacaCapabilityProbe>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
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

app.Run();

public partial class Program;
