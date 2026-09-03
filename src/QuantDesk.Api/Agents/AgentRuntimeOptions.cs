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
            TimeSpan.FromSeconds(45), TimeSpan.FromHours(1),
            new PolicyBounds(
                0.60, MinimumNetEdgeFloor(), 0.05, 0.35, allowedExperts.ToHashSet()),
            Path.GetFullPath(Environment.GetEnvironmentVariable("QUANTDESK_AGENT_STORE_PATH")
                ?? Path.Combine(AppContext.BaseDirectory, "agent-runs.json")));
    }
}
