using System.Text.Json;
using QuantDesk.Domain.Agents;
using QuantDesk.Domain.Serialization;
using QuantDesk.Api.PaperTrading;
using QuantDesk.Runtime.Persistence;
using QuantDesk.Runtime.Research;
using QuantDesk.Runtime.Time;

namespace QuantDesk.Api.Agents;

public sealed record AgentPlaneSnapshot(
    bool Enabled,
    string State,
    string? LastRole,
    string? LastEvidenceId,
    string? Reason,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<AgentRunRecord> RecentRuns);

public sealed class AgentPlaneState(
    AgentRuntimeOptions options, AgentRunStore store, IRuntimeClock clock)
{
    private readonly Lock _gate = new();
    private string _state = options.Enabled ? "starting" : "disabled";
    private string? _role;
    private string? _evidence;
    private string? _reason = options.Enabled ? null : "AGENT_PROVIDER_DISABLED";
    private DateTimeOffset _updated = clock.UtcNow;

    public void Update(string state, AgentRole? role = null, string? evidence = null, string? reason = null)
    {
        lock (_gate) { _state = state; _role = role?.ToString(); _evidence = evidence; _reason = reason; _updated = clock.UtcNow; }
    }

    public AgentPlaneSnapshot Snapshot()
    {
        lock (_gate) return new(options.Enabled, _state, _role, _evidence, _reason, _updated, store.ListAll().Take(20).ToArray());
    }
}

/// <summary>Runs read-only Review then Research work from completed durable executions.</summary>
public sealed class AgentOrchestrationService(
    AgentRuntimeOptions options,
    SpotExecutionStore executions,
    ReviewAgent review,
    ResearchAgent research,
    PolicyAgent policy,
    ResearchArtifactState artifacts,
    ShadowSignalLog shadow,
    AgentRunStore runs,
    AgentPlaneState state,
    IRuntimeClock clock,
    ILogger<AgentOrchestrationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) { state.Update("disabled", reason: "AGENT_PROVIDER_DISABLED"); return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunCycleAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                state.Update("degraded", reason: exception.GetType().Name);
                logger.LogError(exception, "Agent evidence cycle failed.");
            }
            await Task.Delay(options.CycleInterval, stoppingToken);
        }
    }

    internal async Task RunCycleAsync(CancellationToken token)
    {
        SpotExecutionRecord? execution = executions.ListCompleted()
            .OrderByDescending(item => item.CompletedAt).FirstOrDefault(item =>
                !runs.Contains(AgentRole.Review, item.ExecutionId));
        if (execution is null) { state.Update("idle", reason: "NO_UNREVIEWED_COMPLETED_EXECUTION"); return; }

        ReviewAgentInput reviewInput = BuildReviewInput(execution);
        ReviewAgentOutput reviewOutput = await review.RunAsync(reviewInput, token);
        Save(AgentRole.Review, execution.ExecutionId, reviewOutput);

        string reviewEvidenceId = $"review:{execution.ExecutionId}";
        ResearchAgentInput researchInput = new(
            AgentEvaluationMode.ForwardOnly, [], [$"execution:{execution.ExecutionId}"],
            [reviewEvidenceId], new Dictionary<string, double>(), []);
        ResearchHypothesisProposal researchOutput = await research.RunAsync(researchInput, token);
        Save(AgentRole.Research, reviewEvidenceId, researchOutput);

        ResearchArtifactSnapshot artifact = artifacts.Snapshot();
        string[] shadowEvidence = shadow.Summarise().Keys.Order(StringComparer.Ordinal).ToArray();
        if (!artifact.Ready || artifact.Forecast is null ||
            !int.TryParse(artifact.Forecast.ExpertId, out int validatedExpert) ||
            !options.PolicyBounds.AllowedExperts.Contains(validatedExpert) || shadowEvidence.Length == 0)
        {
            state.Update("complete", AgentRole.Research, reviewEvidenceId,
                "POLICY_AWAITING_VALIDATED_EXPERT_EVIDENCE");
            return;
        }

        long currentVersion = runs.ListAll().Count(item => item.Role == AgentRole.Policy);
        PolicyAgentInput policyInput = new(
            new HashSet<int> { validatedExpert },
            artifact.StrategyFamily ?? "verified-artifact",
            shadowEvidence,
            "deterministic-risk-governor",
            currentVersion);
        ValidatedPolicyProposal policyOutput = await policy.RunAsync(policyInput, token);
        Save(AgentRole.Policy, artifact.ArtifactId!, policyOutput);
    }

    private void Save<T>(AgentRole role, string evidenceId, T output)
    {
        DateTimeOffset now = clock.UtcNow;
        runs.Save(new AgentRunRecord(
            $"{role.ToString().ToLowerInvariant()}:{evidenceId}", role, evidenceId, "complete",
            options.Model, JsonSerializer.Serialize(output, ContractJson.Web), null, now, now));
        state.Update("complete", role, evidenceId);
    }

    private static ReviewAgentInput BuildReviewInput(SpotExecutionRecord execution)
    {
        var events = new List<(DateTimeOffset Time, string Type)>
        {
            (execution.CreatedAt, "created"),
            (execution.EntryFinalFillAt ?? execution.CreatedAt, "entry"),
            (execution.ExitFinalFillAt ?? execution.CompletedAt ?? execution.CreatedAt, "exit"),
            (execution.CompletedAt ?? execution.CreatedAt, "complete")
        };
        EpisodeTraceStep[] trace = events.OrderBy(item => item.Time).Select((item, index) =>
            new EpisodeTraceStep(index + 1, item.Time, item.Type, execution.ExecutionId,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes($"{execution.ExecutionId}:{item.Type}:{item.Time:O}")))))
            .ToArray();
        long episodeId = (long)(BitConverter.ToUInt64(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(execution.ExecutionId)), 0)
            & long.MaxValue);
        if (episodeId == 0) episodeId = 1;
        return new ReviewAgentInput(
            episodeId, AgentEvaluationMode.ForwardOnly, trace, [], $"strategy:{execution.StrategyId}",
            $"cost:{execution.ExecutionId}", $"risk:{execution.ExecutionId}",
            $"execution:{execution.ExecutionId}", $"market-path:{execution.ExecutionId}");
    }
}
