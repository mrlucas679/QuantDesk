using System.Globalization;
using QuantDesk.Domain.Agents;
using QuantDesk.Domain.Numerics;

namespace QuantDesk.Api.Agents;

public sealed record AgentRuntimeOptions(
    bool Enabled,
    Uri? BaseUri,
    string Model,
    string? ApiKey,
    TimeSpan CycleInterval,
    TimeSpan RequestTimeout,

    /// <summary>
    /// How much thinking budget the model should spend, when it takes such a parameter.
    ///
    /// Null means the field is omitted entirely, which is the right default: an unknown parameter
    /// is ignored by some OpenAI-compatible providers and rejected by others, so sending one
    /// nobody asked for turns a working configuration into a 400.
    ///
    /// Worth setting for a reasoning model. GLM-5.3-Flash documents that this defaults to
    /// <c>max</c> when absent, and these agents read a small JSON document and return a small JSON
    /// document -- there is nothing here worth a maximum thinking budget, and paying for one buys
    /// latency and tokens rather than a better answer.
    /// </summary>
    string? ReasoningEffort,
    TimeSpan PolicyLease,
    PolicyBounds PolicyBounds,
    string StorePath)
{
    /// <summary>
    /// The smallest net edge a proposed policy may require before it is allowed to trade.
    ///
    /// This was one cent. On the default twenty-dollar notional that is five basis points, against
    /// a measured crypto round trip of about sixty -- so a policy proposing it would have been
    /// within bounds while requiring an edge an order of magnitude below what a trade costs, and
    /// below anything this system can measure. A floor that permits a losing policy is not a floor.
    ///
    /// The default here is two dollars, which is ten per cent of the default notional and
    /// comfortably above the measured round trip. It is a placeholder for a number that should come
    /// from the cost evidence rather than from this file, and it is env-overridable so raising it
    /// does not need a deployment. It bounds what an agent may propose; it does not authorise
    /// anything on its own.
    /// </summary>
    private const decimal DefaultMinimumNetEdgeUsd = 2.00m;

    /// <summary>
    /// How long a hosted model is given to answer.
    ///
    /// This was a hardcoded forty-five seconds, and it was not enough. A 31B model on a shared
    /// inference provider took longer than that to finish streaming its reply, so the client
    /// cancelled mid-body: the request had reached the provider, the answer was on its way, and the
    /// timeout threw it away. What the operator saw was a TaskCanceledException and a degraded
    /// agent plane, which reads like a broken endpoint or a bad key rather than a model that is
    /// simply large.
    ///
    /// Two minutes by default, and configurable so a slower or larger model does not need a
    /// deployment. Nothing waits on this: the cycle runs every ten minutes, the agents are
    /// proposal-only, and no trading decision blocks on their answer -- so a generous budget costs
    /// nothing while a mean one silently discards work the provider already did.
    /// </summary>
    private static TimeSpan ParseRequestTimeout()
    {
        string? configured = Environment.GetEnvironmentVariable(
            "QUANTDESK_AGENT_REQUEST_TIMEOUT_SECONDS");

        return int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds)
            && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(120);
    }

    /// <summary>
    /// The configured thinking budget, or nothing when the operator has not asked for one.
    ///
    /// Only the three values GLM documents are passed through. An arbitrary string would be
    /// forwarded to the provider verbatim and rejected there, which surfaces as a failed agent
    /// cycle rather than as the configuration mistake it is.
    /// </summary>
    private static string? NormalisedReasoningEffort()
    {
        string? configured = Environment
            .GetEnvironmentVariable("QUANTDESK_AGENT_REASONING_EFFORT")?.Trim().ToLowerInvariant();

        return configured is "low" or "high" or "max" ? configured : null;
    }

    private static int[] ParseAllowedExperts(string configured)
    {
        var experts = new List<int>();
        foreach (string text in configured.Split(
                     ',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int expert))
            {
                throw new InvalidOperationException(
                    $"QUANTDESK_AGENT_ALLOWED_EXPERTS contains {text}, which is not an expert id.");
            }

            experts.Add(expert);
        }

        return [.. experts];
    }

    private static Usd MinimumNetEdgeFloor()
    {
        string? configured = Environment.GetEnvironmentVariable("QUANTDESK_AGENT_MIN_NET_EDGE_USD");
        if (string.IsNullOrWhiteSpace(configured)) return new Usd(DefaultMinimumNetEdgeUsd);

        if (!decimal.TryParse(configured, NumberStyles.Number, CultureInfo.InvariantCulture,
                out decimal parsed) || parsed <= 0m)
        {
            throw new InvalidOperationException(
                "QUANTDESK_AGENT_MIN_NET_EDGE_USD must be a positive decimal. A non-positive floor "
                + "would let a policy trade for nothing.");
        }

        return new Usd(parsed);
    }

    public static AgentRuntimeOptions FromEnvironment()
    {
        bool enabled = bool.TryParse(Environment.GetEnvironmentVariable("QUANTDESK_AGENTS_ENABLED"), out bool value) && value;
        string? baseUrl = Environment.GetEnvironmentVariable("QUANTDESK_AGENT_BASE_URL");
        Uri? baseUri = string.IsNullOrWhiteSpace(baseUrl) ? null : new Uri(baseUrl, UriKind.Absolute);
        // Parsed with a stated failure rather than a raw FormatException. Configuration that
        // cannot be read should say which variable and why, not surface as a stack trace during
        // host startup.
        int[] allowedExperts = ParseAllowedExperts(
            Environment.GetEnvironmentVariable("QUANTDESK_AGENT_ALLOWED_EXPERTS") ?? "0");
        return new AgentRuntimeOptions(
            enabled, baseUri, Environment.GetEnvironmentVariable("QUANTDESK_AGENT_MODEL") ?? "qwen3:8b",
            Environment.GetEnvironmentVariable("QUANTDESK_AGENT_API_KEY"), TimeSpan.FromMinutes(10),
            ParseRequestTimeout(),
            NormalisedReasoningEffort(),
            TimeSpan.FromHours(1),
            new PolicyBounds(
                0.60, MinimumNetEdgeFloor(), 0.05, 0.35, allowedExperts.ToHashSet()),
            Path.GetFullPath(Environment.GetEnvironmentVariable("QUANTDESK_AGENT_STORE_PATH")
                ?? Path.Combine(AppContext.BaseDirectory, "agent-runs.json")));
    }
}
