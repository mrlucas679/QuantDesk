using System.Net;
using System.Text;
using QuantDesk.Api.Agents;
using QuantDesk.Domain.Agents;
using QuantDesk.Domain.Numerics;

namespace QuantDesk.Api.Tests;

/// <summary>
/// The client that actually talks to the provider, rather than a stand-in for it.
///
/// Why these did not exist and needed to
/// -------------------------------------
/// <c>EnsureNoMutations</c> was already tested -- against a fake client that fabricated a tool call
/// by hand. The guard worked. What nothing checked was whether the real client could ever hand it
/// one, and it could not: <c>AgentCompletion</c> was constructed with an empty tool-call list
/// unconditionally, so a provider returning <c>tool_calls</c> had them dropped before the guard
/// that exists to notice them.
///
/// A check verified only through a double that manufactures the failing input is a check verified
/// against somebody's belief about the failure. These drive the client with real HTTP responses.
/// </summary>
public sealed class AgentCompletionClientTests
{
    [Fact]
    public async Task AToolCallInTheReplyReachesTheMutationGuard()
    {
        // The provider is offered no tools, so a tool call in the reply means it did something
        // nobody asked for. Whether it actually changed anything is not knowable from here, which
        // is why it is reported as a mutation rather than ignored.
        AgentCompletion completion = await Complete(Reply(
            content: "{}",
            toolCalls: """, "tool_calls": [{ "id": "submit_order" }]"""));

        Assert.Single(completion.ToolCalls);
        Assert.True(completion.ToolCalls[0].MutatedExternalState);
        Assert.Throws<InvalidDataException>(() => ReviewAgent.EnsureNoMutations(completion));
    }

    [Fact]
    public async Task AnOrdinaryReplyCarriesNoToolCalls()
    {
        AgentCompletion completion = await Complete(Reply(content: """{"ok":true}"""));

        Assert.Empty(completion.ToolCalls);
        ReviewAgent.EnsureNoMutations(completion);
    }

    [Fact]
    public async Task OfferingAnyToolAtAllIsRefused()
    {
        // Not "no forbidden tool". A deny list fails open on whatever nobody thought to name, and
        // these agents read evidence and return JSON -- there is no tool they should be offered.
        var invocation = new AgentInvocation(
            AgentRole.Review, "prompt", "{}", "Contract",
            new HashSet<string>(StringComparer.Ordinal) { "something_harmless_sounding" });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Client(Reply(content: "{}")).CompleteAsync(invocation, default));
    }

    [Fact]
    public async Task AReplyWithNoChoicesIsADomainErrorRatherThanAnIndexingCrash()
    {
        // Indexing choices[0] blind turned a provider returning nothing into an
        // IndexOutOfRangeException, which reads as a bug here rather than as a bad reply.
        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Complete("""{"choices":[]}"""));

        Assert.Equal("AGENT_RESPONSE_HAS_NO_CHOICES", failure.Message);
    }

    [Fact]
    public async Task AReplyWithNoContentIsRefused()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Complete("""{"choices":[{"message":{"role":"assistant"}}]}"""));
    }

    [Fact]
    public async Task AnOversizedReplyIsRefusedRatherThanParsed()
    {
        // The body comes from a process this one does not control. An unbounded parse over a
        // hostile or malfunctioning provider is a way to lose the trading host to a reply.
        string enormous = Reply(content: new string('x', 2_000_000));

        await Assert.ThrowsAsync<InvalidDataException>(() => Complete(enormous));
    }

    [Fact]
    public async Task ADisabledProviderRefusesBeforeSendingAnything()
    {
        var handler = new StubHandler("""{"choices":[]}""");
        var client = new AgentCompletionClient(
            new HttpClient(handler), Options() with { Enabled = false });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CompleteAsync(Invocation(), default));
        Assert.Equal(0, handler.Requests);
    }

    // ------------------------------------------------------------------------------- fixtures

    private static string Reply(string content, string toolCalls = "")
    {
        string encoded = System.Text.Json.JsonSerializer.Serialize(content);
        return "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":"
            + encoded + toolCalls + "}}]}";
    }

    private static AgentCompletionClient Client(string body) =>
        new(new HttpClient(new StubHandler(body)), Options());

    private static Task<AgentCompletion> Complete(string body) =>
        Client(body).CompleteAsync(Invocation(), default);

    private static AgentInvocation Invocation() => new(
        AgentRole.Review, "prompt", "{}", "Contract", new HashSet<string>(StringComparer.Ordinal));

    private static AgentRuntimeOptions Options() => new(
        Enabled: true,
        BaseUri: new Uri("http://localhost:11434/"),
        Model: "test",
        ApiKey: null,
        CycleInterval: TimeSpan.FromMinutes(1),
        RequestTimeout: TimeSpan.FromSeconds(5),
        ReasoningEffort: null,
        PolicyLease: TimeSpan.FromHours(1),
        PolicyBounds: new PolicyBounds(0.6, new Usd(2m), 0.05, 0.35, new HashSet<int> { 1 }),
        StorePath: "agent.json");

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
