using QuantDesk.Api.Agents;

namespace QuantDesk.Api.Tests;

/// <summary>
/// Where the agent client actually posts, given whatever the operator wrote in .env.
///
/// The endpoint used to be built with <c>new Uri(baseUri, "v1/chat/completions")</c>, whose result
/// depends on a trailing slash in a way nothing warned about: a base of
/// <c>https://api.featherless.ai/v1/</c> produced <c>/v1/v1/chat/completions</c> and a 404.
///
/// That is the form a provider's quickstart encourages -- Featherless documents
/// <c>base_url="https://api.featherless.ai/v1"</c> -- and a 404 from an inference provider reads as
/// a bad key or a missing model, so the operator checks the credential that was never wrong.
/// </summary>
public sealed class AgentCompletionEndpointTests
{
    [Theory]
    [InlineData("https://api.featherless.ai/")]
    [InlineData("https://api.featherless.ai")]
    [InlineData("https://api.featherless.ai/v1")]
    [InlineData("https://api.featherless.ai/v1/")]
    [InlineData("https://api.featherless.ai/V1/")]
    public void EveryWayOfWritingTheBaseUrlReachesTheSameEndpoint(string baseUrl)
    {
        Uri endpoint = AgentCompletionClient.CompletionsEndpoint(new Uri(baseUrl));

        Assert.Equal("https://api.featherless.ai/v1/chat/completions", endpoint.ToString());
    }

    [Fact]
    public void AProviderBehindAPathPrefixKeepsThePrefix()
    {
        // A gateway or a self-hosted provider mounted under a path is not the same case as a
        // version segment, and discarding it would break the one deployment that needs it.
        Uri endpoint = AgentCompletionClient.CompletionsEndpoint(
            new Uri("https://gateway.internal/inference/"));

        Assert.Equal("https://gateway.internal/inference/v1/chat/completions", endpoint.ToString());
    }

    [Fact]
    public void APortIsPreserved()
    {
        Uri endpoint = AgentCompletionClient.CompletionsEndpoint(new Uri("http://localhost:11434"));

        Assert.Equal("http://localhost:11434/v1/chat/completions", endpoint.ToString());
    }
}
