using System.Text.Json;
using QuantDesk.Api.Agents;
using QuantDesk.Domain.Agents;
using QuantDesk.Domain.Forecasts;
using QuantDesk.Domain.Numerics;
using QuantDesk.Domain.Serialization;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.Tests;

public sealed class AgentServicesTests
{
    [Fact]
    public async Task ThreeAgentsProduceTypedOutputsAndPolicyRemainsOnlyAProposal()
    {
        var reviewOutput = new ReviewAgentOutput(1, [], "sound", "sound", "bounded", ["next test"]);
        var researchOutput = new ResearchHypothesisProposal("H-1", "family", "mechanism", "counter",
            "falsify", ["bars"], "1h", ["spread"], "hold", "walk-forward", ["net pnl"],
            ["negative oos"], ["evidence-1"], false);
        var policyOutput = new PolicyAgentProposal(2, new HashSet<int> { 1 }, 0.7, 1m, 0.01, 0.25);
        var client = new FakeAgentClient(reviewOutput, researchOutput, policyOutput);
        AgentRuntimeOptions options = Options();

        ReviewAgentOutput review = await new ReviewAgent(client).RunAsync(ReviewInput(), default);
        ResearchHypothesisProposal research = await new ResearchAgent(client).RunAsync(
            new ResearchAgentInput(AgentEvaluationMode.ForwardOnly, [], ["evidence"], [],
                new Dictionary<string, double>(), []), default);
        ValidatedPolicyProposal policy = await new PolicyAgent(client, options, new LiveRuntimeClock()).RunAsync(
            new PolicyAgentInput(new HashSet<int> { 1 }, "regime", ["shadow"], "risk", 1), default);

        Assert.True(review.IsValid()); Assert.True(research.IsValid());
        Assert.Equal(2, policy.Policy.Version);
        Assert.Equal(3, client.Invocations.Count);
        Assert.All(client.Invocations, invocation => Assert.Empty(invocation.AllowedTools));
    }

    [Fact]
    public async Task AnyAttemptedExternalMutationInvalidatesAgentOutput()
    {
        var client = new MutatingAgentClient();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ReviewAgent(client).RunAsync(ReviewInput(), default));
    }

    private static ReviewAgentInput ReviewInput() => new(1, AgentEvaluationMode.ForwardOnly,
        [new EpisodeTraceStep(1, DateTimeOffset.UtcNow, "complete", "e-1", "hash")], [],
        "strategy", "cost", "risk", "execution", "market");

    private static AgentRuntimeOptions Options() => new(true, new Uri("http://localhost:11434/"),
        "test", null, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromHours(1),
        new PolicyBounds(0.6, new Usd(0.5m), 0.05, 0.35, new HashSet<int> { 1 }), "agent.json");

    private sealed class FakeAgentClient(params object[] outputs) : IAgentCompletionClient
    {
        private int _index;
        public List<AgentInvocation> Invocations { get; } = [];
        public Task<AgentCompletion> CompleteAsync(AgentInvocation invocation, CancellationToken token)
        {
            Invocations.Add(invocation);
            string output = JsonSerializer.Serialize(outputs[_index++], ContractJson.Web);
            return Task.FromResult(new AgentCompletion("fake", output, []));
        }
    }

    private sealed class MutatingAgentClient : IAgentCompletionClient
    {
        public Task<AgentCompletion> CompleteAsync(AgentInvocation invocation, CancellationToken token) =>
            Task.FromResult(new AgentCompletion("hostile",
                JsonSerializer.Serialize(new ReviewAgentOutput(1, [], "a", "b", "c", []), ContractJson.Web),
                [new AgentToolCall("submit_order", new Dictionary<string, string>(), true)]));
    }
}
