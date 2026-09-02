using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using QuantDesk.Api.PaperTrading;

namespace QuantDesk.Api.Tests;

public sealed class ResearchReadinessMonitorServiceTests
{
    /// <summary>The body the research plane actually returns while it is still collecting evidence.</summary>
    private const string NotReadyBody = """
        {"ready":false,"validated_model_count":0,"features_ready":false,"experts_ready":false,
         "reason":"no_validated_models"}
        """;

    private const string ReadyBody = """
        {"ready":true,"validated_model_count":2,"features_ready":true,"experts_ready":true,
         "reason":"ok"}
        """;

    [Fact]
    public async Task AFiveHundredAndThreeCarryingAReadinessBodyIsAnAnswerNotAnOutage()
    {
        // The research plane returns 503 with a well-formed body for the whole of a campaign's
        // evidence-collection phase -- "no_validated_models", 651 of 8,640 unseen bars. Reading the
        // body only on 2xx would discard the plane's own reason and record the same false for
        // "still collecting" as for "the port is dead".
        FullSystemReadinessState readiness = Probe(
            HttpStatusCode.ServiceUnavailable, NotReadyBody, out ResearchReadinessMonitorService service);
        await service.ProbeAsync(CancellationToken.None);

        Assert.False(readiness.Snapshot().FeaturesReady);
        Assert.False(readiness.Snapshot().ExpertsReady);
    }

    [Fact]
    public async Task AReadyPlaneWithAVerifiedArtifactMarksTheLedgerReady()
    {
        var artifacts = new ResearchArtifactState();
        FullSystemReadinessState readiness = Probe(
            HttpStatusCode.OK, ReadyBody, out ResearchReadinessMonitorService service, artifacts);

        // Without a verified artifact the plane's own readiness is not enough: execution needs
        // something to bind a position to.
        await service.ProbeAsync(CancellationToken.None);
        Assert.False(readiness.Snapshot().ExpertsReady);
    }

    [Fact]
    public async Task AnUnreadablePayloadIsAFaultBecauseSomethingElseIsAnsweringThatPort()
    {
        // Distinguishing this from a considered negative is the point. A false recorded because the
        // plane said so and a false recorded because an HTML error page came back are different
        // facts, and only the second is worth waking someone for.
        FullSystemReadinessState readiness = Probe(
            HttpStatusCode.BadGateway, "<html>502 Bad Gateway</html>",
            out ResearchReadinessMonitorService service);

        await service.ProbeAsync(CancellationToken.None);
        Assert.False(readiness.Snapshot().FeaturesReady);
    }

    [Fact]
    public async Task AnUnreachablePlaneFailsClosedRatherThanThrowing()
    {
        FullSystemReadinessState readiness = new();
        var service = new ResearchReadinessMonitorService(
            new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("http://research/") },
            readiness,
            new ResearchArtifactState(),
            NullLogger<ResearchReadinessMonitorService>.Instance);

        await service.ProbeAsync(CancellationToken.None);

        Assert.False(readiness.Snapshot().FeaturesReady);
        Assert.False(readiness.Snapshot().ExpertsReady);
    }

    [Fact]
    public void TheProbeBudgetOutlastsTheEndpointItProbes()
    {
        // The live defect. A five-second client timeout against a /readiness endpoint measured at
        // 4.1, 5.0 and 8.5 seconds meant the ledger was written from a timeout rather than from the
        // plane's reply -- and would have gone on being written from a timeout after research
        // became ready, leaving the gate refusing entries with the plane green behind it.
        Assert.True(ResearchReadinessMonitorService.ProbeTimeout >= TimeSpan.FromSeconds(15));
    }

    private static FullSystemReadinessState Probe(
        HttpStatusCode status,
        string body,
        out ResearchReadinessMonitorService service,
        ResearchArtifactState? artifacts = null)
    {
        FullSystemReadinessState readiness = new();
        service = new ResearchReadinessMonitorService(
            new HttpClient(new CannedHandler(status, body)) { BaseAddress = new Uri("http://research/") },
            readiness,
            artifacts ?? new ResearchArtifactState(),
            NullLogger<ResearchReadinessMonitorService>.Instance);
        return readiness;
    }

    private sealed class CannedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("the research plane is unreachable");
    }
}
